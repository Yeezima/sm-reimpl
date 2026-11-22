using SmSim.Extras.Warfront.Campaign;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.Campaign;

/// <summary>Warfront plan section 3 and 4: the pure campaign state machine. No World, no networking, no original data.</summary>
public class WarfrontCampaignTests
{
    const ulong P1 = 101, P2 = 102, P3 = 103;

    static WarfrontCampaign Ship(params bool[] rolls)
    {
        var c = new WarfrontCampaign(7, new ForcedRolls(rolls));
        c.Join(P1, out _);
        c.Embark();
        return c;
    }

    static void Fight(WarfrontCampaign c, WarfrontTarget t, WarfrontOutcome o)
    {
        Assert.True(c.TryDeploy(c.Leader!.Value, t, out var err), err);
        c.BeginBattle();
        c.Resolve(o);
    }

    // Win lane 0 three times (the other lanes hold every roll) and leave it cleared.
    static void ClearLane0(WarfrontCampaign c)
    {
        var f = ((ForcedRolls)c.RollSource);
        Assert.NotNull(f);
        for (int i = 0; i < 3; i++) Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Win);
    }

    [Fact]
    public void Run_starts_at_node_1_with_nothing_advanced()
    {
        var c = Ship();
        for (int l = 0; l < 3; l++) { Assert.Equal(1, c.Front(l)); Assert.False(c.Advanced(l)); }
        Assert.False(c.HqMandatory);
        Assert.Equal(WarfrontPhase.Ship, c.Phase);
        Assert.Equal(3, c.AvailableTargets().Count);
    }

    [Fact]
    public void A_lost_roll_holds_a_never_advanced_lane_at_the_floor()
    {
        var c = Ship(true, true); // lanes 1 and 2 both lose
        Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Win);
        Assert.Equal(1, c.Front(1));
        Assert.Equal(1, c.Front(2));
        Assert.All(c.Rolls, r => { Assert.True(r.Lost); Assert.True(r.HeldAtFloor); });
        Assert.False(c.HqMandatory);
    }

    [Fact]
    public void A_wipe_holds_a_never_advanced_lane_at_node_1_and_still_rolls_the_others()
    {
        var c = Ship(false, false);
        Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Wipe);
        Assert.Equal(1, c.Front(0));
        Assert.False(c.Advanced(0));
        Assert.Equal(2, c.Rolls.Count);
        Assert.Equal(WarfrontPhase.Ship, c.Phase);
        Assert.False(c.HqMandatory);
    }

    [Fact]
    public void After_one_win_a_lane_can_reach_zero_and_force_the_hq()
    {
        // win lane 0 -> 2 (rolls hold), then wipe it twice (-> 1 -> 0)
        var c = Ship(false, false, false, false, false, false);
        Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Win);
        Assert.Equal(2, c.Front(0));
        Assert.True(c.Advanced(0));
        Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Wipe);
        Assert.Equal(1, c.Front(0));
        Assert.False(c.HqMandatory);
        Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Wipe);
        Assert.Equal(0, c.Front(0));
        Assert.True(c.HqMandatory);
        Assert.Equal(new[] { WarfrontTarget.Hq }, c.AvailableTargets());
        Assert.False(c.TryDeploy(P1, WarfrontTarget.Node(1), out _));
    }

    [Fact]
    public void A_lost_roll_can_break_through_an_advanced_lane()
    {
        // advance lane 1 by winning it, then fight lane 0 (win) and lose lane 1's roll twice... lane 1 is at 2 -> 1 -> 0
        var c = Ship(false, false, /* dep2: lanes 1,2 */ true, false, /* dep3 */ true, false);
        Fight(c, WarfrontTarget.Node(1), WarfrontOutcome.Win); // lane 1 -> 2, rolls lanes 0,2 hold
        Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Win); // rolls lane 1 lost (2->1), lane 2 held
        Assert.Equal(1, c.Front(1));
        Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Win); // rolls lane 1 lost (1->0), lane 2 held
        Assert.Equal(0, c.Front(1));
        Assert.True(c.HqMandatory);
    }

    [Fact]
    public void A_win_advances_the_lane_and_clearing_node_3_unlocks_the_fortress()
    {
        var c = Ship(Enumerable.Repeat(false, 6).ToArray());
        Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Win);
        Assert.Equal(2, c.Front(0));
        Assert.DoesNotContain(c.AvailableTargets(), t => t.Kind == WarfrontTargetKind.Fortress);
        Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Win);
        Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Win);
        Assert.Equal(4, c.Front(0));
        Assert.Contains(WarfrontTarget.Fortress(0), c.AvailableTargets());
        Assert.DoesNotContain(WarfrontTarget.Node(0), c.AvailableTargets());
    }

    [Fact]
    public void Rolls_are_once_per_other_lane_deterministic_and_never_after_hq_or_on_the_ship()
    {
        var a = new WarfrontCampaign(99);
        var b = new WarfrontCampaign(99);
        foreach (var c in new[] { a, b }) { c.Join(P1, out _); c.Embark(); Fight(c, WarfrontTarget.Node(2), WarfrontOutcome.Win); }
        Assert.Equal(2, a.Rolls.Count);
        Assert.Equal(new[] { 0, 1 }, a.Rolls.Select(r => r.Lane).ToArray());
        Assert.All(a.Rolls, r => Assert.Equal(1, r.Deployment));
        Assert.Equal(a.Rolls.Select(r => r.Lost), b.Rolls.Select(r => r.Lost));
        Assert.Equal(a.StateHash(), b.StateHash());

        // idle on the ship: nothing rolls
        int rolls = a.Rolls.Count;
        a.Embark(); a.BeginBattle(); a.Resolve(WarfrontOutcome.Win); a.WarbossKilled();
        Assert.Equal(rolls, a.Rolls.Count);

        // the hash varies with seed, deployment and lane across a sample (not constant)
        var h = HashedRolls.Instance;
        var seen = new HashSet<bool>();
        for (int d = 1; d < 40; d++) for (int l = 0; l < 3; l++) seen.Add(h.Lost(5, d, l));
        Assert.Equal(2, seen.Count);
        Assert.Equal(h.Lost(5, 3, 1), h.Lost(5, 3, 1));
    }

    [Fact]
    public void Hq_defence_never_rolls()
    {
        var c = BreakthroughCampaign();
        int before = c.Rolls.Count;
        Fight(c, WarfrontTarget.Hq, WarfrontOutcome.Win);
        Assert.Equal(before, c.Rolls.Count);
    }

    // Lane 0 advanced then wiped down to 0 -> HQ mandatory. Rolls all hold.
    static WarfrontCampaign BreakthroughCampaign()
    {
        var c = Ship(Enumerable.Repeat(false, 8).ToArray());
        Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Win);
        Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Wipe);
        Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Wipe);
        Assert.True(c.HqMandatory);
        return c;
    }

    [Fact]
    public void Hq_win_sets_every_front_to_node_2_keeps_advanced_and_clears_the_flag()
    {
        var c = BreakthroughCampaign();
        Fight(c, WarfrontTarget.Hq, WarfrontOutcome.Win);
        for (int l = 0; l < 3; l++) Assert.Equal(2, c.Front(l));
        Assert.True(c.Advanced(0));
        Assert.False(c.Advanced(1));
        Assert.False(c.HqMandatory);
        Assert.Equal(WarfrontPhase.Ship, c.Phase);
    }

    [Fact]
    public void Hq_loss_ends_the_run()
    {
        var c = BreakthroughCampaign();
        Fight(c, WarfrontTarget.Hq, WarfrontOutcome.Wipe);
        Assert.Equal(WarfrontPhase.Lost, c.Phase);
        Assert.True(c.IsOver);
        Assert.Empty(c.AvailableTargets());
    }

    [Fact]
    public void Fortress_wipe_drops_the_cleared_lane_to_3_locks_the_fortress_and_rolls_the_others()
    {
        var c = Ship(Enumerable.Repeat(false, 10).ToArray());
        ClearLane0(c);
        int rollsBefore = c.Rolls.Count;
        Fight(c, WarfrontTarget.Fortress(0), WarfrontOutcome.Wipe);
        Assert.Equal(3, c.Front(0));
        Assert.DoesNotContain(c.AvailableTargets(), t => t.Kind == WarfrontTargetKind.Fortress);
        Assert.Equal(rollsBefore + 2, c.Rolls.Count);
        Assert.Equal(WarfrontPhase.Ship, c.Phase);
    }

    [Fact]
    public void Another_cleared_lane_keeps_the_fortress_open_after_a_wipe()
    {
        var c = Ship(Enumerable.Repeat(false, 20).ToArray());
        for (int l = 0; l < 2; l++) for (int i = 0; i < 3; i++) Fight(c, WarfrontTarget.Node(l), WarfrontOutcome.Win);
        Fight(c, WarfrontTarget.Fortress(0), WarfrontOutcome.Wipe);
        Assert.Equal(3, c.Front(0));
        Assert.Contains(WarfrontTarget.Fortress(1), c.AvailableTargets());
    }

    [Fact]
    public void A_cleared_lane_that_loses_a_roll_drops_to_3()
    {
        var c = Ship(false, false, false, false, false, false, /* dep4 */ true, false);
        ClearLane0(c);
        Fight(c, WarfrontTarget.Node(1), WarfrontOutcome.Win); // lane 0 loses its roll: 4 -> 3
        Assert.Equal(3, c.Front(0));
    }

    [Fact]
    public void Warboss_kill_wins_the_run_without_rolls()
    {
        var c = Ship(Enumerable.Repeat(false, 10).ToArray());
        ClearLane0(c);
        int rolls = c.Rolls.Count;
        Assert.True(c.TryDeploy(P1, WarfrontTarget.Fortress(0), out _));
        c.BeginBattle();
        c.WarbossKilled();
        Assert.Equal(WarfrontPhase.Won, c.Phase);
        Assert.Equal(rolls, c.Rolls.Count);
    }

    [Fact]
    public void Only_the_leader_deploys_and_only_from_the_ship()
    {
        var c = Ship();
        c.Join(P2, out _);
        Assert.False(c.TryDeploy(P2, WarfrontTarget.Node(0), out _));
        Assert.False(c.TryDeploy(P1, WarfrontTarget.Fortress(0), out _));
        Assert.True(c.TryDeploy(P1, WarfrontTarget.Node(0), out _));
        Assert.False(c.TryDeploy(P1, WarfrontTarget.Node(1), out _)); // already travelling
    }

    [Fact]
    public void Leadership_follows_join_order_passes_on_leave_and_returns()
    {
        var c = Ship();
        c.Join(P2, out _); c.Join(P3, out _);
        Assert.Equal(P1, c.Leader);
        c.Leave(P1);
        Assert.Equal(P2, c.Leader);
        c.Leave(P2);
        Assert.Equal(P3, c.Leader);
        c.Join(P2, out var k2);
        Assert.Equal(WarfrontJoinKind.Returning, k2);
        Assert.Equal(P2, c.Leader);
        c.Join(P1, out _);
        Assert.Equal(P1, c.Leader);
        Assert.Equal(new[] { P1, P2, P3 }, c.JoinOrder.ToArray());
        c.Leave(P1); c.Leave(P2); c.Leave(P3);
        Assert.Null(c.Leader);
    }

    [Fact]
    public void A_fifth_human_is_refused()
    {
        var c = Ship();
        for (ulong k = 2; k <= 4; k++) Assert.True(c.Join(k + 200, out _));
        Assert.False(c.Join(999, out _));
    }

    [Fact]
    public void A_late_joiner_starts_at_level_one_and_the_join_grant_gives_no_xp_while_a_returning_player_keeps_his_own_state()
    {
        var c = Ship();
        c.Join(P2, out _);
        c.AddXp(P1, 10000);
        c.AddXp(P2, 4000);
        c.Join(P3, "Three", out var kind);
        Assert.Equal(WarfrontJoinKind.New, kind);
        var p3 = c.Player(P3)!;
        Assert.Equal(0, p3.Xp);
        Assert.False(p3.JoinGrantApplied);
        Assert.Equal("Three", p3.Name);

        Assert.False(c.ApplyJoinGrant(P3)); // owner 2026-11-22: no median grant any more, the call only sets the flag
        Assert.Equal(0, p3.Xp);
        Assert.Equal(1, p3.Level);
        Assert.True(p3.JoinGrantApplied);
        Assert.False(c.ApplyJoinGrant(P3));
        Assert.Equal(0, p3.Xp);

        c.AddXp(P3, 1600);
        Assert.True(c.TryTakePick(P3, 2));
        long xp = p3.Xp; int picks = p3.PicksTaken;
        c.Leave(P3);
        c.Join(P3, out kind);
        Assert.Equal(WarfrontJoinKind.Returning, kind);
        Assert.False(c.ApplyJoinGrant(P3));
        Assert.Equal(xp, c.Player(P3)!.Xp);
        Assert.Equal(picks, c.Player(P3)!.PicksTaken);
        Assert.Equal(new[] { 2 }, c.Player(P3)!.Cards.ToArray());
    }

    [Fact]
    public void The_join_grant_gives_nothing_whoever_is_connected()
    {
        var c = Ship();
        c.Join(P2, out _); c.Join(P3, out _);
        c.AddXp(P1, 9000); c.AddXp(P2, 3000);
        c.Leave(P1);
        Assert.False(c.ApplyJoinGrant(P3));
        Assert.Equal(0, c.Player(P3)!.Xp);

        var d = Ship();
        d.Join(P2, out _);
        d.AddXp(P1, 6000);
        d.Leave(P1);
        d.Leave(P2);
        d.Join(P3, out _); d.Leave(P3);
        Assert.False(d.ApplyJoinGrant(P3, new[] { P1 }));
        Assert.Equal(0, d.Player(P3)!.Xp);
        Assert.True(d.Player(P3)!.JoinGrantApplied);
    }

    [Fact]
    public void The_first_player_is_granted_nothing_and_unknown_keys_are_refused_without_throwing()
    {
        var c = Ship();
        Assert.False(c.ApplyJoinGrant(P1));
        Assert.True(c.Player(P1)!.JoinGrantApplied);
        Assert.False(c.AddXp(777, 5));
        Assert.False(c.TryTakePick(777, 1));
        Assert.False(c.ApplyJoinGrant(777));
    }

    [Fact]
    public void Four_connected_refuse_a_fifth_new_or_returning_and_a_leaver_frees_a_seat()
    {
        var c = Ship(); // P1 connected
        c.Join(P2, out _); c.Join(P3, out _); c.Join(304, out _);
        Assert.False(c.Join(305, out _));
        c.Leave(P3);
        Assert.True(c.Join(305, out var k5)); // admitted with three connected
        Assert.Equal(WarfrontJoinKind.New, k5);
        Assert.Equal(5, c.JoinOrder.Count); // the leaver stays in the run
        Assert.NotNull(c.Player(P3));
        Assert.False(c.Join(P3, out var back)); // returning, but the cap holds
        Assert.Equal(WarfrontJoinKind.Returning, back);
        c.Leave(305);
        Assert.True(c.Join(P3, out _));
    }

    [Fact]
    public void The_cap_equals_musous_co_op_cap()
    {
        Assert.Equal(SmSim.Extras.Musou.Campaign.TfwCoop.MaxHumans, WarfrontCampaign.MaxHumans);
    }

    [Fact]
    public void A_wipe_at_the_floor_is_recorded_in_the_history()
    {
        var c = Ship(false, false);
        Fight(c, WarfrontTarget.Node(0), WarfrontOutcome.Wipe);
        var h = c.History.Single();
        Assert.Equal(1, h.Before);
        Assert.Equal(1, h.After);
        Assert.True(h.HeldAtFloor);

        var d = Ship(false, false, false, false);
        Fight(d, WarfrontTarget.Node(0), WarfrontOutcome.Win);
        Fight(d, WarfrontTarget.Node(0), WarfrontOutcome.Wipe);
        Assert.Equal(2, d.History[1].Before);
        Assert.Equal(1, d.History[1].After);
        Assert.False(d.History[1].HeldAtFloor);
    }

    [Fact]
    public void A_pick_cannot_be_taken_twice_for_one_level()
    {
        var c = Ship();
        c.AddXp(P1, WarfrontCampaign.XpForLevel(2));
        Assert.Equal(2, c.Player(P1)!.Level);
        Assert.Equal(1, c.Player(P1)!.PendingPicks);
        Assert.True(c.TryTakePick(P1, 1));
        Assert.False(c.TryTakePick(P1, 1));
        Assert.Equal(1500, WarfrontCampaign.XpForLevel(2));
        Assert.Equal(31500, WarfrontCampaign.XpForLevel(10));
    }

    [Fact]
    public void A_restart_in_a_battle_resumes_on_the_ship_with_nothing_rolled()
    {
        var c = Ship(true, true);
        Assert.True(c.TryDeploy(P1, WarfrontTarget.Node(0), out _));
        c.BeginBattle();
        var back = WarfrontSave.FromJson(c.ToJson(), new ForcedRolls());
        back.ResumeAfterRestart();
        Assert.Equal(WarfrontPhase.Ship, back.Phase);
        Assert.Equal(0, back.Deployments);
        Assert.Empty(back.Rolls);
        Assert.Null(back.CurrentTarget);
        Assert.Null(back.Leader);
    }

    [Fact]
    public void A_travel_save_stays_in_travel_with_its_target_so_the_stage_process_can_land()
    {
        var c = Ship();
        Assert.True(c.TryDeploy(P1, WarfrontTarget.Node(2), out _));
        var back = WarfrontSave.FromJson(c.ToJson());
        back.ResumeAfterRestart();
        Assert.Equal(WarfrontPhase.Travel, back.Phase);
        Assert.Equal(WarfrontTarget.Node(2), back.CurrentTarget);
        back.Join(P1, out _);
        back.BeginBattle();
        back.Resolve(WarfrontOutcome.Win);
        Assert.Equal(2, back.Front(2));
    }
}
