using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Coop;
using SmSim.Extras.Warfront.Progression;
using SmSim.Extras.Warfront.Squad;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.Coop;

/// <summary>Slice W12: the co-op rules, pure (no server, no sockets). A real campaign and a real roster store behind a host double that counts what the rules asked for.</summary>
public class WarfrontCoopTests
{
    const ulong A = 1, B = 2, C = 3, D = 4, E = 5, F = 6;

    sealed class Host : IWarfrontCoopHost
    {
        public SquadRosters Rosters = new();
        public int Retainers, Saves, Holds, Rebinds;
        public List<(ulong Key, int Player)> Rebound = new();
        public bool HasRoster(ulong key) => Rosters.Has(key);
        public void AddStartingRetainer(ulong key) { Rosters.AddOwner(key); Retainers++; }
        public void HoldSquad(ulong key) => Holds++;
        public void RebindSquad(ulong key, int player) { Rebinds++; Rebound.Add((key, player)); }
        public void Save() => Saves++;
    }

    sealed class Rig
    {
        public WarfrontCampaign C = new(7);
        public Host H = new();
        public WarfrontCoop Co;
        public int NextPlayer = 1;
        public Rig() => Co = new WarfrontCoop(C, H);
        public Rig(WarfrontCampaign c, Host h) { C = c; H = h; Co = new WarfrontCoop(c, h); NextPlayer = 20; }

        public WarfrontCoopJoin Join(ulong key, string? name = null) => Co.Connect(key, name ?? ("P" + key), NextPlayer++);

        /// <summary>The founders A and B on the ship (rosters made as the server does at a first join), A on 5000 XP (level 3), B on 3000 (level 2).</summary>
        public static Rig Founders()
        {
            var r = new Rig();
            r.Join(A); r.Join(B);
            r.H.Rosters.AddOwner(A); r.H.Rosters.AddOwner(B);
            r.C.AddXp(A, 5000); r.C.AddXp(B, 3000);
            r.C.Embark();
            return r;
        }

        public void Deploy() { Assert.True(C.TryDeploy(C.Leader!.Value, WarfrontTarget.Node(0), out var err), err); C.BeginBattle(); }
        public void Win() { C.Resolve(WarfrontOutcome.Win); Co.ShipStop(); }

        /// <summary>After one fought battle: the founders aboard again, the first deployment done.</summary>
        public static Rig AfterFirstBattle() { var r = Founders(); r.Deploy(); r.Win(); r.TakeAllPicks(A); r.TakeAllPicks(B); return r; }

        public void TakeAllPicks(ulong key) { while (C.TryTakePick(key, 1)) { } Co.PicksChanged(key); }
    }

    static (long Xp, int Level, int Picks, int Pending, string Cards, int Roster, bool Grant, bool Confirmed, int JoinedAt) Snap(Rig r, ulong key)
    {
        var p = r.C.Player(key)!;
        return (p.Xp, p.Level, p.PicksTaken, p.PendingPicks, string.Join(",", p.Cards), r.H.Rosters.Count(key), p.JoinGrantApplied, p.ClassConfirmed, p.JoinedAtDeployment);
    }

    // ---------------------------------------------------------------- 1. leader and join order

    [Fact]
    public void Leader_follows_the_join_order_and_comes_back_to_the_first_to_return()
    {
        var r = new Rig();
        r.Join(A); r.Join(B); r.Join(C);
        Assert.Equal(A, r.Co.Leader);
        Assert.Equal(0, r.Co.LeaderChanges);   // the first leader is no change
        r.Co.Left(A);
        Assert.Equal(B, r.Co.Leader);
        r.Co.Left(B);
        Assert.Equal(C, r.Co.Leader);
        r.Join(A);
        Assert.Equal(A, r.Co.Leader);
        Assert.Equal(3, r.Co.LeaderChanges);
        Assert.Equal(new[] { A, B, C }, r.C.JoinOrder);
        Assert.Equal(new[] { A, B, C }, WarfrontSave.FromJson(r.C.ToJson()).JoinOrder);
    }

    [Fact]
    public void A_leader_change_is_announced_for_a_few_seconds_to_every_client_and_does_not_cancel_a_deploy()
    {
        var r = Rig.Founders();
        Assert.False(r.Co.LeaderChangedNow);
        Assert.True(r.C.TryDeploy(A, WarfrontTarget.Node(0), out _));   // the deploy is under way (Travel)
        r.Co.Left(A);
        Assert.Equal(WarfrontPhase.Travel, r.C.Phase);   // not cancelled
        Assert.Equal(B, r.Co.Leader);
        Assert.True(r.Co.LeaderChangedNow);
        Assert.True(r.Co.ViewFor(B).YouLead && r.Co.ViewFor(B).LeaderChanged);
        Assert.Equal("P2", r.Co.ViewFor(B).LeaderName);
        r.Co.Tick(WarfrontCoop.LeaderChangedSeconds + 0.1);
        Assert.False(r.Co.ViewFor(B).LeaderChanged);
    }

    // ---------------------------------------------------------------- 2. returning key, 3. new key on the ship

    [Fact]
    public void A_founder_before_the_first_deployment_is_not_gated_and_joins_as_a_player()
    {
        var r = Rig.Founders();
        Assert.Equal(WarfrontCoopRole.Player, r.Co.RoleOf(A));
        Assert.True(r.C.Player(A)!.ClassConfirmed);
        Assert.Equal(0, r.C.Player(A)!.JoinedAtDeployment);
        var c = r.Join(C);   // a third founder on the first ship stop
        Assert.True(c.Admitted);
        Assert.Equal(WarfrontCoopRole.Player, c.Role);
        Assert.Null(r.Co.DeployRefusal());
        Assert.Equal(0, r.H.Retainers);
    }

    [Fact]
    public void A_new_key_on_the_ship_after_the_first_deployment_is_gated_at_once_with_its_grant()
    {
        var r = Rig.AfterFirstBattle();
        var c = r.Join(C);
        Assert.Equal(WarfrontCoopRole.AwaitClass, c.Role);
        var p = r.C.Player(C)!;
        Assert.Equal(1, p.JoinedAtDeployment);
        Assert.False(p.ClassConfirmed);
        Assert.Equal(4000, p.Xp);   // the median of 5000 and 3000
        Assert.Equal(3, p.Level);
        Assert.Equal(1, r.H.Rosters.Count(C));
        Assert.True(r.Co.ViewFor(C).GrantAppliedThisStop);
        Assert.Equal(90, r.Co.ClassGateSecondsLeft(C));
        Assert.NotNull(r.Co.DeployRefusal());
    }

    // ---------------------------------------------------------------- 4. new key in a battle

    [Fact]
    public void A_new_key_in_a_battle_is_held_aboard_spectates_the_leader_and_earns_nothing()
    {
        var r = Rig.Founders();
        r.Deploy();
        var c = r.Join(C);
        Assert.True(c.Admitted);
        Assert.Equal(WarfrontCoopRole.SpectatingJoiner, c.Role);
        Assert.False(c.FlyIn);
        Assert.True(r.Co.IsHeld(3));
        Assert.False(r.Co.AllowsLateJoin(3));
        Assert.True(r.Co.AllowsLateJoin(1));    // a founder is not held
        Assert.True(r.Co.AllowsLateJoin(99));   // unknown ids (bots) are not asked
        Assert.False(r.Co.EarnsXp(C));
        Assert.Equal(new[] { A, B }, r.Co.XpEarners().ToArray());
        Assert.False(r.C.Player(C)!.JoinGrantApplied);   // the grant waits for the ship stop
        Assert.Equal(0, r.C.Player(C)!.Xp);
        Assert.Equal(0, r.H.Retainers);
        // the watch target is the leader (player id 1), stepped through living humans then marines
        var cands = new[] { new WarfrontWatch(WarfrontWatchKind.Player, 1), new WarfrontWatch(WarfrontWatchKind.Player, 2), new WarfrontWatch(WarfrontWatchKind.Marine, 7) };
        Assert.Equal(cands[0], r.Co.ResolveWatch(C, cands));
        Assert.Equal(cands[1], r.Co.Step(C, +1, cands));
        Assert.Equal(cands[2], r.Co.Step(C, +1, cands));
        Assert.Equal(cands[0], r.Co.Step(C, +1, cands));   // wraps
        Assert.Equal(cands[2], r.Co.Step(C, -1, cands));
        // the target is gone: back to the leader
        Assert.Equal(cands[0], r.Co.ResolveWatch(C, new[] { cands[0], cands[1] }));
        Assert.Equal(default, r.Co.Step(C, 1, Array.Empty<WarfrontWatch>()));
    }

    // ---------------------------------------------------------------- 5. ship stop and the median

    [Fact]
    public void The_grant_is_at_the_ship_stop_not_the_join_and_a_second_stop_grants_nothing()
    {
        var r = Rig.Founders();
        r.Deploy();
        r.Join(C);
        r.C.Resolve(WarfrontOutcome.Win);
        Assert.Equal(0, r.C.Player(C)!.Xp);
        r.Co.ShipStop();
        Assert.Equal(4000, r.C.Player(C)!.Xp);
        Assert.Equal(WarfrontCoopRole.AwaitClass, r.Co.RoleOf(C));
        Assert.True(r.Co.ViewFor(C).GrantAppliedThisStop);
        Assert.Equal(1, r.H.Retainers);
        Assert.True(r.H.Saves > 0);
        r.Co.ShipStop();   // a second stop
        Assert.Equal(4000, r.C.Player(C)!.Xp);
        Assert.Equal(1, r.H.Retainers);
        Assert.False(r.Co.ViewFor(C).GrantAppliedThisStop);
    }

    [Theory]
    [InlineData(new long[] { 5000, 3000, 9000 }, 5000)]          // odd count: the middle one
    [InlineData(new long[] { 5000, 3000 }, 4000)]                // even: the mean of the two middle ones
    [InlineData(new long[] { 3001 }, 3001)]                      // one other
    public void The_median_of_the_others(long[] xps, long expected)
    {
        var c = new WarfrontCampaign(7);
        var h = new Host();
        var co = new WarfrontCoop(c, h);
        for (int i = 0; i < xps.Length; i++) { co.Connect((ulong)(10 + i), "F" + i, i + 1); c.AddXp((ulong)(10 + i), xps[i]); }
        c.Embark();
        c.TryDeploy(c.Leader!.Value, WarfrontTarget.Node(0), out _);
        c.BeginBattle();
        co.Connect(99, "New", 50);
        c.Resolve(WarfrontOutcome.Win);
        co.ShipStop();
        Assert.Equal(expected, c.Player(99)!.Xp);
    }

    [Fact]
    public void The_grant_never_lowers_and_two_joiners_at_one_stop_read_the_same_figures()
    {
        var r = Rig.Founders();
        r.Deploy();
        r.Join(C); r.C.AddXp(C, 9000);   // it earned more elsewhere before
        r.Join(D);
        r.C.Resolve(WarfrontOutcome.Win);
        r.Co.ShipStop();
        Assert.Equal(9000, r.C.Player(C)!.Xp);   // never lowered
        Assert.Equal(4000, r.C.Player(D)!.Xp);   // D's median counts A and B only: C's grant was pending at the same stop
    }

    // ---------------------------------------------------------------- 6. class confirm, 7. picks, 8. deploy gate

    [Fact]
    public void Confirm_only_works_in_AwaitClass_then_picks_then_player()
    {
        var r = Rig.AfterFirstBattle();
        Assert.False(r.Co.ConfirmClass(A));   // a Player: nothing
        r.Join(C);
        Assert.Equal(2, r.C.Player(C)!.PendingPicks);   // level 3: two catch-up cards
        Assert.True(r.Co.ConfirmClass(C));
        Assert.True(r.C.Player(C)!.ClassConfirmed);
        Assert.Equal(WarfrontCoopRole.AwaitPicks, r.Co.RoleOf(C));
        Assert.False(r.Co.ConfirmClass(C));   // not twice
        Assert.True(r.C.TryTakePick(C, 1)); r.Co.PicksChanged(C);
        Assert.Equal(WarfrontCoopRole.AwaitPicks, r.Co.RoleOf(C));
        Assert.True(r.C.TryTakePick(C, 2)); r.Co.PicksChanged(C);
        Assert.Equal(WarfrontCoopRole.Player, r.Co.RoleOf(C));
        Assert.Null(r.Co.DeployRefusal());
    }

    [Fact]
    public void A_joiner_with_no_pending_picks_goes_straight_to_player_on_confirm()
    {
        var r = Rig.Founders();
        r.C.AddXp(A, -r.C.Player(A)!.Xp); r.C.AddXp(B, -r.C.Player(B)!.Xp);   // both on 0 XP
        r.Deploy(); r.Win();
        r.Join(C);
        Assert.True(r.Co.ConfirmClass(C));
        Assert.Equal(WarfrontCoopRole.Player, r.Co.RoleOf(C));
    }

    [Fact]
    public void The_deploy_gate_names_who_is_awaited_and_why_and_opens_when_they_are_done()
    {
        var r = Rig.AfterFirstBattle();
        r.Join(C, "Cato");
        r.Join(D, "Dorn");
        Assert.Equal("Waiting for Cato (class), Dorn (class)", r.Co.DeployRefusal());
        r.Co.ConfirmClass(C);
        Assert.Equal("Waiting for Cato (2 cards), Dorn (class)", r.Co.DeployRefusal());
        r.Co.ConfirmClass(D);
        r.C.TryTakePick(C, 1); r.C.TryTakePick(C, 1); r.Co.PicksChanged(C);
        r.C.TryTakePick(D, 1); r.Co.PicksChanged(D);
        Assert.Equal("Waiting for Dorn (1 card)", r.Co.DeployRefusal());
        r.C.TryTakePick(D, 1); r.Co.PicksChanged(D);
        Assert.Null(r.Co.DeployRefusal());
        Assert.Empty(r.Co.ViewFor(A).Awaited);
    }

    [Fact]
    public void A_gated_player_who_disconnects_stops_blocking_and_resumes_the_same_role()
    {
        var r = Rig.AfterFirstBattle();
        r.Join(C);
        Assert.NotNull(r.Co.DeployRefusal());
        var before = Snap(r, C);
        r.Co.Left(C);
        Assert.Null(r.Co.DeployRefusal());
        r.Join(C);
        Assert.Equal(WarfrontCoopRole.AwaitClass, r.Co.RoleOf(C));
        Assert.Equal(before, Snap(r, C));
        Assert.Equal(1, r.H.Retainers);   // no second retainer
    }

    [Fact]
    public void The_class_gate_confirms_after_90_seconds_and_the_picker_timeout_covers_the_picks()
    {
        var r = Rig.AfterFirstBattle();
        r.Join(C);
        r.Co.Tick(89);
        Assert.Equal(WarfrontCoopRole.AwaitClass, r.Co.RoleOf(C));
        Assert.Equal(1, r.Co.ClassGateSecondsLeft(C));
        r.Co.Tick(1.01);
        Assert.True(r.C.Player(C)!.ClassConfirmed);
        Assert.Equal(WarfrontCoopRole.AwaitPicks, r.Co.RoleOf(C));
        // the picker's own 20 s timeout takes card 1 for each open offer
        var picker = new WarfrontPicker(r.C);
        double now = 0;
        while (r.C.Player(C)!.PendingPicks > 0)
        {
            Assert.NotNull(picker.TryOpen(C, now, aboard: true, lull: false, 1, false));
            now += WarfrontProgressionRules.PickTimeoutSeconds;
            Assert.Single(picker.Tick(now));
            r.Co.PicksChanged(C);
        }
        Assert.Equal(WarfrontCoopRole.Player, r.Co.RoleOf(C));
        Assert.Null(r.Co.DeployRefusal());
    }

    [Fact]
    public void The_class_clock_runs_only_while_the_key_is_connected()
    {
        var r = Rig.AfterFirstBattle();
        r.Join(C);
        r.Co.Tick(80);
        r.Co.Left(C);
        r.Co.Tick(500);
        r.Join(C);
        Assert.Equal(WarfrontCoopRole.AwaitClass, r.Co.RoleOf(C));
        Assert.InRange(r.Co.ClassGateSecondsLeft(C), 1, 90);
    }

    [Fact]
    public void Aboard_the_picker_skips_the_lull_and_the_gap_so_catch_up_cards_open_one_after_another()
    {
        var r = Rig.AfterFirstBattle();
        r.Join(C); r.Co.ConfirmClass(C);
        var picker = new WarfrontPicker(r.C);
        var first = picker.TryOpen(C, 10, aboard: true, lull: false, 1, false);
        Assert.NotNull(first);
        Assert.True(picker.TryPick(C, 0, first!.PickIndex, out _, out _));
        var second = picker.TryOpen(C, 10.5, aboard: true, lull: false, 1, false);   // no 45 s gap, no lull
        Assert.NotNull(second);
        Assert.Equal(first.PickIndex + 1, second!.PickIndex);
    }

    // ---------------------------------------------------------------- 10. cap

    [Fact]
    public void A_fifth_human_is_refused_with_nothing_created()
    {
        var r = new Rig();
        r.Join(A); r.Join(B); r.Join(C); r.Join(D);
        var no = r.Join(E);
        Assert.False(no.Admitted);
        Assert.Equal("The squad is full (4 of 4)", no.Refusal);
        Assert.False(r.C.IsConnected(E));
        Assert.Null(r.C.Player(E));
        Assert.Equal(4, r.C.JoinOrder.Count);
    }

    [Fact]
    public void A_full_squad_refuses_first_come_first_served_and_a_freed_place_goes_to_whoever_connects_first()
    {
        var r = new Rig();
        r.Join(A); r.Join(B); r.Join(C); r.Join(D);
        r.Co.Left(B);                              // B knows the run
        Assert.True(r.Join(F).Admitted);           // an unknown key takes the free place: no priority list
        Assert.False(r.Join(B).Admitted);          // B (known) is refused: the squad is full
        r.Co.Left(D);
        Assert.True(r.Join(B).Admitted);
        Assert.False(r.Join(E).Admitted);
    }

    [Fact]
    public void A_key_the_campaign_already_connected_still_gets_its_role_from_Connect()
    {
        var r = Rig.AfterFirstBattle();
        r.C.Join(C, "Cato", out _);                // W5a's identify path joined through the campaign first
        var j = r.Co.Connect(C, "Cato", 9);
        Assert.True(j.Admitted);
        Assert.Equal(WarfrontCoopRole.AwaitClass, j.Role);
        Assert.True(r.C.Player(C)!.JoinGrantApplied);
        Assert.Equal(1, r.H.Retainers);
        Assert.Equal(WarfrontCoopRole.AwaitClass, r.Co.Connect(C, "Cato", 9).Role);   // a second call changes nothing
        Assert.Equal(1, r.H.Retainers);
        // in a battle: held
        var b = Rig.Founders(); b.Deploy();
        b.C.Join(C, "Cato", out _);
        Assert.Equal(WarfrontCoopRole.SpectatingJoiner, b.Co.Connect(C, "Cato", 9).Role);
        Assert.True(b.Co.IsHeld(9));
    }

    [Fact]
    public void A_fresh_coop_object_per_run_and_a_ship_stop_on_a_finished_run_is_refused_with_a_clear_text()
    {
        var r = Rig.AfterFirstBattle();
        var json = r.C.ToJson().Replace("\"Phase\":\"Ship\"", "\"Phase\":\"Won\"");
        var over = new Rig(WarfrontSave.FromJson(json), r.H);
        var ex = Assert.Throws<InvalidOperationException>(() => over.Co.ShipStop());
        Assert.Contains("new WarfrontCoop", ex.Message);
    }

    [Fact]
    public void The_seams_are_attached_to_the_battle_rules_and_the_xp_predicate()
    {
        var r = Rig.Founders(); r.Deploy(); r.Join(C);
        var rules = new WarfrontBattleRules();
        var eligible = r.Co.AttachTo(rules);
        Assert.True(rules.CoopHeld!(3)); Assert.False(rules.CoopHeld(1));
        Assert.False(rules.LateJoinAllowed!(3)); Assert.True(rules.LateJoinAllowed(1));
        Assert.False(eligible(C)); Assert.True(eligible(A));
    }

    [Fact]
    public void Picks_queued_aboard_hold_the_deploy_for_founders_too_and_a_restart_changes_nothing()
    {
        var r = Rig.Founders();
        r.Deploy(); r.C.Resolve(WarfrontOutcome.Win); r.Co.ShipStop();   // A has 2 picks queued, B 1
        Assert.Equal(WarfrontCoopRole.AwaitPicks, r.Co.RoleOf(A));
        Assert.Equal("Waiting for P1 (2 cards), P2 (1 card)", r.Co.DeployRefusal());
        // a restart: a new rules object over the loaded campaign, the keys connect again
        var loaded = WarfrontSave.FromJson(r.C.ToJson());
        loaded.ResumeAfterRestart();
        var r2 = new Rig(loaded, r.H);
        r2.Join(A); r2.Join(B);
        Assert.Equal("Waiting for P1 (2 cards), P2 (1 card)", r2.Co.DeployRefusal());
        // the picker's timeout takes card 1 every 20 s: at most 40 s for two picks
        var picker = new WarfrontPicker(loaded);
        double now = 0;
        for (int i = 0; i < 2; i++)
        {
            if (loaded.Player(A)!.PendingPicks > 0) { Assert.NotNull(picker.TryOpen(A, now, true, false, 1, false)); }
            if (loaded.Player(B)!.PendingPicks > 0) { Assert.NotNull(picker.TryOpen(B, now, true, false, 1, false)); }
            now += WarfrontProgressionRules.PickTimeoutSeconds;
            picker.Tick(now);
            r2.Co.PicksChanged(A); r2.Co.PicksChanged(B);
        }
        Assert.Equal(40, now);
        Assert.Null(r2.Co.DeployRefusal());
    }

    [Fact]
    public void Picks_earned_while_aboard_move_a_player_to_AwaitPicks_on_the_next_tick()
    {
        var r = Rig.AfterFirstBattle();
        Assert.Equal(WarfrontCoopRole.Player, r.Co.RoleOf(A));
        r.C.AddXp(A, 3000);
        r.Co.Tick(0.1);
        Assert.Equal(WarfrontCoopRole.AwaitPicks, r.Co.RoleOf(A));
    }

    [Fact]
    public void The_cap_can_be_lowered_by_the_option()
    {
        var r = new Rig();
        r.Co.HumanCap = 2;
        r.Join(A); r.Join(B);
        var c = r.Join(C);
        Assert.False(c.Admitted);
        Assert.Equal("The squad is full (2 of 2)", c.Refusal);
    }

    // ---------------------------------------------------------------- leaver's squad, returning in a battle (rulings 4, 5)

    [Fact]
    public void A_leaver_in_a_battle_holds_the_squad_and_a_living_leaver_is_flown_in_and_rebound()
    {
        var r = Rig.Founders();
        r.Deploy();
        r.Co.Left(B, WarfrontLeaveState.Alive);
        Assert.Equal(1, r.H.Holds);
        var back = r.Join(B);
        Assert.True(back.FlyIn);
        Assert.Equal(0f, back.SpectatorSeconds);
        Assert.Equal(WarfrontCoopRole.Player, back.Role);
        Assert.Equal((B, 3), r.H.Rebound.Single());
        Assert.False(r.Co.IsHeld(3));
    }

    [Fact]
    public void A_downed_or_spectating_leaver_comes_back_as_a_spectator_never_with_a_free_life()
    {
        var r = Rig.Founders();
        r.Deploy();
        r.Co.Left(B, WarfrontLeaveState.Downed);
        var d = r.Join(B);
        Assert.False(d.FlyIn);
        Assert.Equal(WarfrontBattleRules.SpectatorSeconds, d.SpectatorSeconds);
        r.Co.Left(B, WarfrontLeaveState.Spectating, 23f);
        var s = r.Join(B);
        Assert.False(s.FlyIn);
        Assert.Equal(23f, s.SpectatorSeconds);
        Assert.Equal(2, r.H.Rebinds);
    }

    [Fact]
    public void A_confirmed_key_that_was_not_in_the_battle_is_flown_in_with_its_squad_and_a_gated_one_stays_held()
    {
        var r = Rig.AfterFirstBattle();
        r.Join(C); r.Co.ConfirmClass(C);   // C joined on the ship and took its class, picks pending
        r.Join(D);                         // D never confirms
        r.Co.Left(C); r.Co.Left(D);
        r.Deploy();                        // the squad deploys without them (they do not block)
        var c = r.Join(C);
        Assert.True(c.FlyIn);
        Assert.Equal(WarfrontCoopRole.Player, c.Role);
        Assert.Contains((C, 5), r.H.Rebound.Select(x => (x.Key, x.Player)).Where(x => x.Item1 == C).Select(x => (x.Item1, x.Item2)));
        var d = r.Join(D);
        Assert.False(d.FlyIn);
        Assert.Equal(WarfrontCoopRole.SpectatingJoiner, d.Role);
        r.C.Resolve(WarfrontOutcome.Win); r.Co.ShipStop();
        Assert.Equal(WarfrontCoopRole.AwaitPicks, r.Co.RoleOf(C));   // its picks are derived again aboard
        Assert.Equal(WarfrontCoopRole.AwaitClass, r.Co.RoleOf(D));
    }

    // ---------------------------------------------------------------- the matrix: leave and rejoin in every state

    public enum St { Lobby, Ship, Travel, BattleAlive, BattleDowned, BattleSpectating, SpectatingJoiner, AwaitClass, AwaitPicksOffer, Won, Lost }

    [Theory]
    [InlineData(St.Lobby)] [InlineData(St.Ship)] [InlineData(St.Travel)] [InlineData(St.BattleAlive)] [InlineData(St.BattleDowned)] [InlineData(St.BattleSpectating)]
    [InlineData(St.SpectatingJoiner)] [InlineData(St.AwaitClass)] [InlineData(St.AwaitPicksOffer)] [InlineData(St.Won)] [InlineData(St.Lost)]
    public void Leave_and_rejoin_changes_nothing_in_every_state(St state)
    {
        Rig r;
        ulong key = B;
        var leave = WarfrontLeaveState.NotInBattle;
        float rest = 0f;
        switch (state)
        {
            case St.Lobby: r = new Rig(); r.Join(A); r.Join(B); r.H.Rosters.AddOwner(B); r.C.AddXp(B, 2000); break;
            case St.Ship: r = Rig.Founders(); break;
            case St.Travel: r = Rig.Founders(); Assert.True(r.C.TryDeploy(A, WarfrontTarget.Node(0), out _)); break;
            case St.BattleAlive: r = Rig.Founders(); r.Deploy(); leave = WarfrontLeaveState.Alive; break;
            case St.BattleDowned: r = Rig.Founders(); r.Deploy(); leave = WarfrontLeaveState.Downed; break;
            case St.BattleSpectating: r = Rig.Founders(); r.Deploy(); leave = WarfrontLeaveState.Spectating; rest = 31f; break;
            case St.SpectatingJoiner: r = Rig.Founders(); r.Deploy(); r.Join(C); key = C; break;
            case St.AwaitClass: r = Rig.AfterFirstBattle(); r.Join(C); key = C; break;
            case St.AwaitPicksOffer: r = Rig.AfterFirstBattle(); r.Join(C); r.Co.ConfirmClass(C); key = C; break;
            default:
            {
                var ship = Rig.AfterFirstBattle();
                var json = ship.C.ToJson().Replace("\"Phase\":\"Ship\"", "\"Phase\":\"" + (state == St.Won ? "Won" : "Lost") + "\"");
                r = new Rig(WarfrontSave.FromJson(json), ship.H);
                r.Join(A); r.Join(B);
                break;
            }
        }
        var picker = new WarfrontPicker(r.C);
        WarfrontOpenOffer? offer = null;
        if (state == St.AwaitPicksOffer) offer = picker.TryOpen(key, 5, aboard: true, lull: false, r.H.Rosters.Count(key), false);
        var before = Snap(r, key);
        int retainers = r.H.Retainers;

        r.Co.Left(key, leave, rest);
        picker.Leave(key);
        // a process restart in between where the app does it (Continue): the save is written and read back with nobody connected
        if (state is St.Travel or St.Ship or St.AwaitClass or St.AwaitPicksOffer or St.Lobby)
        {
            var loaded = WarfrontSave.FromJson(r.C.ToJson());
            loaded.ResumeAfterRestart();
            r = new Rig(loaded, r.H);
        }
        var back = r.Join(key);

        Assert.True(back.Admitted);
        Assert.Equal(WarfrontJoinKind.Returning, back.Kind);
        Assert.Equal(before, Snap(r, key));
        Assert.Equal(retainers, r.H.Retainers);   // no roster created, no grant again
        switch (state)
        {
            case St.BattleAlive: Assert.True(back.FlyIn); break;
            case St.BattleDowned: Assert.Equal(60f, back.SpectatorSeconds); Assert.False(back.FlyIn); break;
            case St.BattleSpectating: Assert.Equal(31f, back.SpectatorSeconds); Assert.False(back.FlyIn); break;
            case St.SpectatingJoiner: Assert.Equal(WarfrontCoopRole.SpectatingJoiner, back.Role); break;
            case St.AwaitClass: Assert.Equal(WarfrontCoopRole.AwaitClass, back.Role); break;
            case St.AwaitPicksOffer:
                Assert.Equal(WarfrontCoopRole.AwaitPicks, back.Role);   // derived from the saved facts: the restart changes nothing
                var again = new WarfrontPicker(r.C).TryOpen(key, 9, aboard: true, lull: false, r.H.Rosters.Count(key), false);
                Assert.NotNull(again);
                Assert.Equal(offer!.PickIndex, again!.PickIndex);
                Assert.Equal(offer.Cards, again.Cards);   // the same offer
                break;
            default: Assert.False(back.FlyIn); break;
        }
    }

    [Fact]
    public void AwaitPicks_survives_a_leave_and_return_in_the_same_process()
    {
        var r = Rig.AfterFirstBattle();
        r.Join(C); r.Co.ConfirmClass(C);
        var before = Snap(r, C);
        r.Co.Left(C);
        r.Join(C);
        Assert.Equal(WarfrontCoopRole.AwaitPicks, r.Co.RoleOf(C));
        Assert.Equal(before, Snap(r, C));
    }

    // ---------------------------------------------------------------- the campaign save

    [Fact]
    public void Version_4_round_trips_the_two_new_fields_and_the_keyless_archive_copies_them()
    {
        var r = Rig.AfterFirstBattle();
        r.Join(C);
        var json = r.C.ToJson();
        Assert.Contains("\"JoinedAtDeployment\":1", json);
        Assert.Contains("\"ClassConfirmed\":false", json);
        var back = WarfrontSave.FromJson(json);
        Assert.Equal(1, back.Player(C)!.JoinedAtDeployment);
        Assert.False(back.Player(C)!.ClassConfirmed);
        Assert.Equal(r.C.StateHash(), back.StateHash());
        var dir = Path.Combine(Path.GetTempPath(), "wfcoop-" + Guid.NewGuid().ToString("N"));
        string keyless;
        try { keyless = File.ReadAllText(WarfrontSave.Archive(dir, r.C, DateTime.UtcNow)); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        Assert.Contains("\"JoinedAtDeployment\":1", keyless);
        Assert.Contains("\"ClassConfirmed\":false", keyless);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Older_saves_load_with_every_key_confirmed(int version)
    {
        var r = Rig.AfterFirstBattle();
        r.Join(C);
        var node = System.Text.Json.Nodes.JsonNode.Parse(r.C.ToJson())!.AsObject();
        node["Version"] = version;
        foreach (var p in node["Players"]!.AsArray()) { var o = p!.AsObject(); o.Remove("JoinedAtDeployment"); o.Remove("ClassConfirmed"); }
        if (version == 1) node.Remove("Stats");
        var back = WarfrontSave.FromJson(node.ToJsonString());
        Assert.All(back.Players, p => Assert.True(p.ClassConfirmed));
        Assert.All(back.Players, p => Assert.Equal(0, p.JoinedAtDeployment));
        // even an old file that carried a false is read as confirmed (no gate existed then)
        node["Players"]!.AsArray()[0]!.AsObject()["ClassConfirmed"] = false;
        Assert.True(WarfrontSave.FromJson(node.ToJsonString()).Player(A)!.ClassConfirmed);
    }

    [Fact]
    public void A_key_joining_once_the_first_deployment_began_must_confirm_but_a_founder_before_it_need_not()
    {
        var c = new WarfrontCampaign(1);
        c.Join(A, out _); c.Embark();
        Assert.False(c.FirstDeploymentBegun);
        Assert.True(c.TryDeploy(A, WarfrontTarget.Node(0), out _));
        Assert.True(c.FirstDeploymentBegun);   // Travel
        c.Join(B, out _);
        Assert.False(c.Player(B)!.ClassConfirmed);
        Assert.True(c.Player(A)!.ClassConfirmed);
    }

    // ---------------------------------------------------------------- hooks are no-ops without the rules

    [Fact]
    public void The_run_answers_allow_and_not_held_without_the_rules()
    {
        var run = new SmSim.Extras.Musou.Campaign.TfwRun(SmNet.Tests.Extras.Musou.Campaign.TfwTestArena.Field);
        Assert.True(run.WarfrontCoopAllowsLate(5));
        Assert.False(run.WarfrontCoopHeld(5));
        var r = Rig.Founders();
        r.Deploy();
        r.Join(C);
        run.WarfrontCoopRules = r.Co;
        Assert.False(run.WarfrontCoopAllowsLate(3));
        Assert.True(run.WarfrontCoopHeld(3));
        Assert.True(run.WarfrontCoopAllowsLate(1));
    }
}
