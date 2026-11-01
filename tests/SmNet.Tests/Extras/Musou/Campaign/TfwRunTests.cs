using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>The run (TF4, design sections D and F): scripted through the real World, the AI Marines faked by <see cref="TfwRun.RetainerUp"/>.</summary>
public class TfwRunTests
{
    const int Human = TfwRig.Human;

    [Fact]
    public void A_run_starts_with_5_transport_and_4_supplies_and_shows_nothing_until_a_deploy()
    {
        var w = TfwRig.War();
        Assert.Equal((5, 4), (w.Tfw!.Transport, w.Tfw.Supplies));
        Assert.Equal(TfwRunPhase.Hub, w.Tfw.Phase);
        Assert.Equal(default, w.Musou!.Director);
    }

    [Fact]
    public void A_deploy_costs_1_takes_5_s_and_lands_a_fresh_pistol_life_at_the_landing_zone()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = 0, Primary = 0 }));
        TfwRig.Command(w, TfwCommand.Deploy, TfwLane.B);
        TfwRig.Steps(w, 2);
        Assert.Equal(TfwRunPhase.Travel, run.Phase);
        Assert.Equal(4, run.Transport);
        Assert.Equal(MusouPhase.Campaign, w.Musou!.Director.Phase);
        TfwRig.Seconds(w, TfwBattleRules.TravelSeconds - 0.3f);
        Assert.Equal(TfwRunPhase.Travel, run.Phase);
        TfwRig.Seconds(w, 0.4f);
        Assert.Equal(TfwRunPhase.Battle, run.Phase);
        Assert.Equal(TfwLane.B, run.War.BattleLane);
        Assert.Null(w.NextLoadout(Human));                                  // the customizer's Setup applied
        var p = w.Players[Human];
        Assert.True(Vector3.Distance(p.Position with { Z = 0f }, TfwTestArena.LandingZone) < 3f, $"{p.Position}");
        Assert.Equal((-1, WeaponSlot.Pistol), (p.Primary, p.Slot));
        Assert.Equal(6, w.TfwGuards!.SquadsAlive);                          // 7 at Imperial 100, at most 6 in the field
        Assert.Equal(run.Battle!.Layout.Positions[0], run.ActivePosition);
    }

    [Theory]
    [InlineData(TfwCommand.Deploy, 3)]   // no such lane
    [InlineData(TfwCommand.RequestPickup, 0)]
    [InlineData(TfwCommand.CancelPickup, 0)]
    public void Commands_out_of_place_do_nothing(TfwCommand command, byte argument)
    {
        var w = TfwRig.War();
        Assert.True(w.Tfw!.Enqueue(Human, new TfwCommandRecord(1, command, argument)));
        TfwRig.Steps(w, 2);
        Assert.Equal((TfwRunPhase.Hub, 5, 0), (w.Tfw.Phase, w.Tfw.Transport, w.Tfw.QueuedCommands));
    }

    [Fact]
    public void Orders_need_a_battle_and_their_points_stay_in_the_arena()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var seen = new List<TfwCommandRecord>();
        run.OrderSeen = (_, c) => seen.Add(c);
        run.Enqueue(Human, new TfwCommandRecord(1, TfwCommand.Hold, 0, new Vector3(1f, 2f, 0f)));
        TfwRig.Steps(w, 2);
        Assert.Empty(seen);                                                  // the hub: refused
        TfwRig.Deploy(w);
        run.Enqueue(Human, new TfwCommandRecord(2, TfwCommand.Attack, 0, new Vector3(3277f, -3277f, -3277f)));
        run.Enqueue(TfwContracts.PuppetIdFirst, new TfwCommandRecord(3, TfwCommand.Attack, 0, Vector3.Zero));   // nobody in the field
        TfwRig.Steps(w, 2);
        var order = Assert.Single(seen);
        Assert.Equal(TfwTestArena.Field.Clamp(new Vector3(3277f, -3277f, -3277f)), order.Point);
    }

    [Fact]
    public void Hold_and_attack_take_the_clamped_point_a_retainer_stands_after_a_deploy_and_leaves_with_the_battle()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        run.RetainerUp = null;
        TfwRig.Deploy(w);
        var retainer = Assert.IsType<Marine>(run.RetainerOf(Human));
        run.Enqueue(Human, new TfwCommandRecord(1, TfwCommand.Hold, 0, new Vector3(3277f, 10f, 0f)));
        TfwRig.Steps(w, 1);
        Assert.Equal(new Vector3(TfwTestArena.Field.Bounds.Max.X, 10f, 0f), retainer.HoldAt);
        run.Enqueue(Human, new TfwCommandRecord(2, TfwCommand.Attack, 0, new Vector3(5f, -3277f, 0f)));
        TfwRig.Steps(w, 1);
        Assert.Equal(new Vector3(5f, TfwTestArena.Field.Bounds.Min.Y, 0f), retainer.AttackAt);
        run.Enqueue(Human, new TfwCommandRecord(3, TfwCommand.Follow, 0));
        TfwRig.Steps(w, 1);
        Assert.Null(retainer.AttackAt);
        Assert.True(w.TfwKillForTests(Human));                               // the real retainer: a window opens, the respawn is held
        TfwRig.Steps(w, 2);
        Assert.True(run.ReviveWanted(Human));
        var body = w.Players[Human].Position;
        for (int t = 0; t < 25 * 30 && !w.Players[Human].Alive; t++) w.Step(_ => default);
        Assert.True(w.Players[Human].Alive, "the retainer came and revived");
        Assert.Equal(body.X, w.Players[Human].Position.X, 2);                // where it fell, not in the Stormbird
        Assert.Equal(4, run.Supplies);
        TfwRig.Steps(w, 2);
        Assert.False(run.ReviveWanted(Human));
    }

    [Fact]
    public void The_last_human_leaving_takes_the_marines_with_it()
    {
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        Assert.NotEmpty(w.Tfw!.Marines);                                     // a retainer and the lane's lord with his own
        w.Remove(Human);
        TfwRig.Steps(w, 2);
        Assert.Equal(MatchState.Warmup, w.State);
        Assert.Empty(w.Tfw.Marines);
        Assert.Empty(w.Players);
    }

    [Fact]
    public void A_human_who_joins_mid_battle_waits_in_the_stormbird_and_blocks_no_extraction()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var battle = TfwRig.Deploy(w);
        w.Spawn(2, w.AssignClass(0));
        TfwRig.Seconds(w, 1f);
        Assert.Equal([Human], run.InField);
        Assert.True(TfwRig.InStormbird(w.Players[2].Position), $"{w.Players[2].Position}");
        Assert.True(run.Enqueue(2, new TfwCommandRecord(1, TfwCommand.RequestPickup, 0)));
        TfwRig.Steps(w, 2);
        Assert.Equal(TfwRunPhase.Battle, run.Phase);                         // not in the field: no say
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.3f);
        Assert.Equal((TfwRunPhase.Hub, 2), (run.Phase, run.Supplies));
        TfwRig.Deploy(w);
        Assert.Equal([Human, 2], run.InField);                               // both go on the next deploy
    }

    [Fact]
    public void Without_transport_a_pickup_and_a_deploy_are_refused_until_the_hq_sends_one()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        for (int k = 0; k < 2; k++)
        {
            TfwRig.Deploy(w);
            TfwRig.Command(w, TfwCommand.RequestPickup);
            TfwRig.Seconds(w, TfwBattleRules.PickupSeconds + 0.2f);
        }
        TfwRig.Deploy(w);
        Assert.Equal(0, run.Transport);
        TfwRig.Command(w, TfwCommand.RequestPickup);
        TfwRig.Steps(w, 2);
        Assert.Equal(TfwRunPhase.Battle, run.Phase);                         // no way out by air
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.3f);
        Assert.Equal((TfwRunPhase.Hub, 0, 2), (run.Phase, run.Transport, run.Supplies));
        TfwRig.Command(w, TfwCommand.Deploy);
        TfwRig.Steps(w, 2);
        Assert.Equal((TfwRunPhase.Hub, 0), (run.Phase, run.Transport));
        while (run.War.Clock < TfwBattleRules.HqTransportSeconds) run.War.Step();
        TfwRig.Seconds(w, 1.2f);
        Assert.Equal(1, run.Transport);
        TfwRig.Deploy(w);
        Assert.Equal((TfwRunPhase.Battle, 0), (run.Phase, run.Transport));
    }

    [Fact]
    public void A_players_projectile_does_not_outlive_the_battle()
    {
        var slow = new ProjectileSpec("t", 0.5f, 120f, "raycast", 0f, [], false, false, false, 0, 0f, 0f, 0, null, null);
        var pistol = TfwRig.Config.Weapons[1] with { Delivery = DeliveryKind.Projectile, Projectile = slow };
        var w = TfwRig.War(config: TfwRig.Config with { Weapons = [TfwRig.Config.Weapons[0], pistol] });
        w.Tfw!.RetainerUp = _ => false;
        TfwRig.Deploy(w);
        for (int k = 0; k < 8 && w.Projectiles.Count == 0; k++)
        {
            w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire));
            w.Step(_ => default);
        }
        Assert.NotEmpty(w.Projectiles);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.3f);
        Assert.Equal(TfwRunPhase.Hub, w.Tfw.Phase);
        Assert.Empty(w.Projectiles);
        Assert.Equal(0, w.Players[Human].ProjectilesOut);
    }

    [Fact]
    public void A_flood_of_deploys_is_drained_each_tick_and_starves_no_order()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var seen = new List<TfwCommandRecord>();
        run.OrderSeen = (_, c) => seen.Add(c);
        TfwRig.Deploy(w);
        for (int k = 0; k < TfwRun.MaxQueuedCommands; k++) Assert.True(run.Enqueue(Human, new TfwCommandRecord((ushort)k, TfwCommand.Deploy, 1)));
        Assert.False(run.Enqueue(Human, new TfwCommandRecord(100, TfwCommand.Follow, 0)));   // full
        TfwRig.Steps(w, 1);
        Assert.Equal(0, run.QueuedCommands);
        Assert.True(run.Enqueue(Human, new TfwCommandRecord(101, TfwCommand.Follow, 0)));
        TfwRig.Steps(w, 1);
        Assert.Single(seen);
        Assert.Equal(TfwLane.A, run.War.BattleLane);
    }

    [Fact]
    public void A_deploy_in_the_field_or_from_outside_the_stormbird_is_refused()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        TfwRig.Deploy(w);
        TfwRig.Command(w, TfwCommand.Deploy, TfwLane.B);
        TfwRig.Steps(w, 2);
        Assert.Equal((TfwRunPhase.Battle, TfwLane.A, 4), (run.Phase, run.War.BattleLane, run.Transport));
        var fresh = TfwRig.War();
        TfwRig.Teleport(fresh, TfwTestArena.LandingZone);
        TfwRig.Command(fresh, TfwCommand.Deploy);
        TfwRig.Steps(fresh, 2);
        Assert.Equal((TfwRunPhase.Hub, 5), (fresh.Tfw!.Phase, fresh.Tfw.Transport));
    }

    [Fact]
    public void A_battle_goes_p1_p2_p3_the_sources_stop_the_commander_falls_and_the_battle_is_won()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var battle = TfwRig.Deploy(w);
        Assert.All(Enumerable.Range(0, 3), s => Assert.True(battle.SourceActive(s)));
        Assert.False(battle.SourceActive(3));                                // the HQ's starts with the commander

        TfwRig.Secure(w, battle, 0);
        Assert.False(battle.SourceActive(0));
        Assert.Equal((4, 4), (run.Transport, run.Supplies));

        TfwRig.Secure(w, battle, 1);
        Assert.Equal((6, 5), (run.Transport, run.Supplies));                 // the depot
        Assert.False(battle.SourceActive(1));

        Assert.Equal(battle.Secured == 3, battle.BossId != 0);               // the squads walk on: the third may fall by itself
        TfwRig.Secure(w, battle, 2);
        Assert.False(battle.SourceActive(2));
        Assert.True(battle.SourceActive(3));                                 // the HQ's stops only at victory
        ushort commander = battle.BossId;
        Assert.NotEqual(0, commander);
        Assert.Equal(battle.Layout.Hq, run.ActivePosition);

        sbyte front = run.War.LaneState(TfwLane.A).Front;
        double ork = run.War.LaneState(TfwLane.A).Ork;
        w.Musou!.Damage(commander, Human, 1e6f, Vector3.Zero, out bool killed);
        Assert.True(killed);
        TfwRig.Steps(w, 2);
        Assert.True(battle.Won);
        Assert.False(battle.SourceActive(3));
        Assert.Equal(front + 1, run.War.LaneState(TfwLane.A).Front);
        Assert.Equal(ork * WarRules.WinOrkFactor, run.War.LaneState(TfwLane.A).Ork, 3);
        int arrivals = battle.Arrivals;
        TfwRig.Seconds(w, 20f);
        Assert.Equal(arrivals, battle.Arrivals);                             // nothing comes any more
    }

    [Fact]
    public void A_pickup_takes_20_s_costs_1_and_leaving_applies_the_formula()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var battle = TfwRig.Deploy(w);
        TfwRig.Secure(w, battle, 0);
        var guards = w.TfwGuards!;
        int squad = guards.NearestSquad(TfwTestArena.LandingZone);
        for (int k = 0; k < 3; k++) guards.Hit(squad, 1e6f, Vector3.Zero);
        TfwRig.KillAll(w);
        TfwRig.Steps(w, 1);
        int lost = guards.Lost, kills = battle.OrksKilled;
        Assert.True(kills >= TfwTestArena.StartAlive, $"{kills}");
        Assert.True(lost >= 3);

        TfwRig.Command(w, TfwCommand.RequestPickup);
        TfwRig.Steps(w, 1);
        Assert.Equal(TfwRunPhase.Pickup, run.Phase);
        Assert.Equal(599, run.PickupTicks);
        Assert.Equal(4, run.Transport);                                      // charged on the way out
        while (run.PickupTicks > 1) w.Step(_ => default);
        var before = run.War.LaneState(TfwLane.A);
        kills = battle.OrksKilled;
        lost = guards.Lost;
        // The last tick: the war second may fall on it, so a shadow of that lane's reinforcements is within the tolerance.
        w.Step(_ => default);
        Assert.Equal(TfwRunPhase.Hub, run.Phase);
        Assert.Equal(3, run.Transport);
        var after = run.War.LaneState(TfwLane.A);
        Assert.Equal((float)WarRules.LeftProgress(battle.Secured), after.Progress, 4);   // the squads walk on: a further point may have fallen meanwhile
        Assert.Equal(before.Ork - 0.05f * kills, after.Ork, 0.1f);
        Assert.Equal(before.Imperial - 0.1f * lost, after.Imperial, 0.1f);
        Assert.True(TfwRig.InStormbird(w.Players[Human].Position), $"{w.Players[Human].Position}");
        Assert.Equal(0, w.Musou!.Count);
        Assert.Equal(0, w.TfwGuards!.Count);
        Assert.Null(run.Battle);
        Assert.Null(run.ActivePosition);
    }

    [Fact]
    public void A_pickup_is_free_after_a_win_and_counts_no_leaving()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        TfwRig.WinBattle(w, TfwLane.A);
        Assert.Equal(6, run.Transport);                                      // 5 - 1 + the depot's 2, no pickup fee
        Assert.Equal(2, run.War.LaneState(TfwLane.A).Front);
    }

    [Fact]
    public void A_pickup_can_be_cancelled_and_a_death_cancels_it()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        TfwRig.Deploy(w);
        TfwRig.Command(w, TfwCommand.RequestPickup);
        TfwRig.Steps(w, 2);
        TfwRig.Command(w, TfwCommand.CancelPickup);
        TfwRig.Steps(w, 2);
        Assert.Equal((TfwRunPhase.Battle, 0), (run.Phase, run.PickupTicks));
        TfwRig.Command(w, TfwCommand.RequestPickup);
        TfwRig.Steps(w, 2);
        Assert.Equal(TfwRunPhase.Pickup, run.Phase);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Steps(w, 2);
        Assert.Equal((TfwRunPhase.Battle, 0), (run.Phase, run.PickupTicks));
        Assert.Equal(4, run.Transport);
    }

    [Fact]
    public void An_extraction_costs_2_supplies_after_3_s_and_counts_as_leaving()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var battle = TfwRig.Deploy(w);
        TfwRig.Secure(w, battle, 0);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds - 0.3f);
        Assert.False(w.Players[Human].Alive);
        Assert.Equal(TfwRunPhase.Battle, run.Phase);
        Assert.True(run.ExtractionTicks > 0);
        TfwRig.Seconds(w, 0.5f);
        Assert.Equal(TfwRunPhase.Hub, run.Phase);
        Assert.Equal(2, run.Supplies);
        Assert.Equal(4, run.Transport);                                      // no pickup fee
        Assert.True(w.Players[Human].Alive);
        Assert.True(TfwRig.InStormbird(w.Players[Human].Position));
        Assert.Equal((float)WarRules.LeftProgress(battle.Secured), run.War.LaneState(TfwLane.A).Progress, 4);
    }

    /// <summary>The run's reserves set by hand (test only: a won battle always holds the depot's supply, so 0 cannot come by itself).</summary>
    static void SetReserves(TfwRun run, int supplies, int? transport = null)
    {
        typeof(TfwRun).GetProperty(nameof(TfwRun.Supplies))!.SetValue(run, supplies);
        if (transport is { } t) typeof(TfwRun).GetProperty(nameof(TfwRun.Transport))!.SetValue(run, t);
    }

    static TfwBattle WinWithoutLeaving(World w)
    {
        var battle = TfwRig.Deploy(w);
        for (int k = 0; k < 3; k++) TfwRig.Secure(w, battle, k);
        TfwRig.KillAll(w);
        TfwRig.Steps(w, 2);
        Assert.True(battle.Won);
        return battle;
    }

    [Theory]
    [InlineData(4, 2)]
    [InlineData(3, 1)]
    [InlineData(2, 0)]
    [InlineData(1, 0)]   // under the price: it takes what is left
    [InlineData(0, 0)]   // nothing left: never below 0
    public void An_extraction_after_a_won_battle_never_ends_the_run(int supplies, int left)
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var battle = WinWithoutLeaving(w);
        var progress = run.War.LaneState(TfwLane.A);
        SetReserves(run, supplies);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Steps(w, 2);
        Assert.True(run.ExtractionTicks > 0);
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.3f);
        Assert.Equal((TfwRunPhase.Hub, left), (run.Phase, run.Supplies));
        Assert.Equal(MatchState.Playing, w.State);
        Assert.True(w.Players[Human].Alive);
        Assert.True(TfwRig.InStormbird(w.Players[Human].Position));
        Assert.Equal(progress.Front, run.War.LaneState(TfwLane.A).Front);    // a won battle does not count as left
        Assert.Equal(progress.Progress, run.War.LaneState(TfwLane.A).Progress, 3);
        TfwRig.Deploy(w);                                                    // the run goes on: no supply is asked for a deploy
        Assert.Equal(TfwRunPhase.Battle, run.Phase);
    }

    [Fact]
    public void A_won_extraction_with_nothing_left_waits_in_the_hub_for_a_transport()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        WinWithoutLeaving(w);
        SetReserves(run, 0, transport: 0);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.3f);
        Assert.Equal((TfwRunPhase.Hub, 0, 0), (run.Phase, run.Transport, run.Supplies));
        TfwRig.Command(w, TfwCommand.Deploy);
        TfwRig.Seconds(w, 3f);
        Assert.Equal(TfwRunPhase.Hub, run.Phase);                            // refused, and nothing ended: the HQ's next transport opens the way
        Assert.Equal(MatchState.Playing, w.State);
    }

    [Fact]
    public void An_extraction_from_a_battle_not_won_under_the_price_still_ends_the_run()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var battle = TfwRig.Deploy(w);
        SetReserves(run, 1);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.3f);
        Assert.Equal((TfwRunPhase.Lost, 1), (run.Phase, run.Supplies));
    }

    [Fact]
    public void A_run_left_short_by_a_won_extraction_is_lost_by_the_next_extraction_not_won()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        WinWithoutLeaving(w);
        SetReserves(run, 1);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.3f);
        Assert.Equal((TfwRunPhase.Hub, 0), (run.Phase, run.Supplies));
        TfwRig.Deploy(w);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.3f);
        Assert.Equal(TfwRunPhase.Lost, run.Phase);
    }

    [Fact]
    public void The_win_aid_fells_only_what_stands_near_and_outside_a_war_does_nothing()
    {
        var w = TfwRig.War();
        Assert.Equal(0, new World(TfwRig.Config).TfwSmiteForTests(Human, Vector3.Zero, 500f));
        var battle = TfwRig.Deploy(w);
        int before = w.Musou!.Alive;
        Assert.Equal(TfwBattleRules.CrowdStart(0, 3), w.TfwSmiteForTests(Human, battle.Layout.Positions[0], 15f));
        Assert.Equal(before - TfwBattleRules.CrowdStart(0, 3), w.Musou.Alive);
    }

    [Fact]
    public void The_stress_aid_tops_up_only_in_a_battle()
    {
        var w = TfwRig.War();
        w.TfwFillForTests(180, 20);
        Assert.Equal(0, w.Musou!.Alive);
        TfwRig.Deploy(w);
        w.TfwFillForTests(180, 20);
        Assert.Equal(180, w.Musou.Alive);
        Assert.Equal(20, w.TfwGuards!.SquadsAlive);
    }

    [Fact]
    public void An_unpaid_extraction_ends_the_run_and_a_new_run_follows_the_delay()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        for (int k = 0; k < 2; k++)
        {
            TfwRig.Deploy(w);
            Assert.True(w.TfwKillForTests(Human));
            TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.3f);
            Assert.Equal(TfwRunPhase.Hub, run.Phase);
        }
        Assert.Equal(0, run.Supplies);
        TfwRig.Deploy(w);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.3f);
        Assert.Equal(TfwRunPhase.Lost, run.Phase);
        Assert.Equal(MatchState.Ended, w.State);
        Assert.Equal(Team.Chaos, w.Winner);
        Assert.False(w.Players[Human].Alive);                                // nobody returns on the end screen
        TfwRig.Seconds(w, w.Rules!.RestartDelay + w.Rules.CountdownTime + 0.5f);
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Equal(TfwRunPhase.Hub, run.Phase);
        Assert.Equal((5, 4), (run.Transport, run.Supplies));
        Assert.True(w.Players[Human].Alive);
        Assert.Equal(0, w.Musou!.Count);
    }

    [Fact]
    public void With_a_retainer_the_fallen_wait_25_s_for_a_revive_and_then_extract()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        run.RetainerUp = _ => true;
        TfwRig.Deploy(w);
        w.TfwDismissMarines();                                               // a stand-in that never comes: the window alone
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Steps(w, 2);
        Assert.True(run.ReviveWanted(Human));
        TfwRig.Seconds(w, 10f);                                              // past the core's 2 s respawn: still down
        Assert.False(w.Players[Human].Alive);
        Assert.True(w.TfwRevive(Human));
        TfwRig.Steps(w, 2);
        Assert.False(run.ReviveWanted(Human));
        Assert.Equal(TfwRunPhase.Battle, run.Phase);
        Assert.Equal(4, run.Supplies);

        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ReviveWindowSeconds - 0.5f);
        Assert.True(run.ReviveWanted(Human));
        Assert.Equal(0, run.ExtractionTicks);
        TfwRig.Seconds(w, 1f);
        Assert.False(run.ReviveWanted(Human));
        Assert.True(run.ExtractionTicks > 0);
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds);
        Assert.Equal((TfwRunPhase.Hub, 2), (run.Phase, run.Supplies));
    }

    [Fact]
    public void A_retainer_lost_closes_the_window()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        bool up = true;
        run.RetainerUp = _ => up;
        TfwRig.Deploy(w);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Steps(w, 2);
        up = false;
        TfwRig.Steps(w, 2);
        Assert.False(run.ReviveWanted(Human));
        Assert.True(run.ExtractionTicks > 0);
    }

    [Fact]
    public void The_hq_falling_ends_the_run()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        for (int k = 0; k < 20000 && run.War.Phase != TfwRunPhase.Lost; k++) run.War.Step();
        Assert.Equal(TfwRunPhase.Lost, run.Phase);
        TfwRig.Steps(w, 2);
        Assert.Equal(MatchState.Ended, w.State);
        Assert.Equal(Team.Chaos, w.Winner);
    }

    [Fact]
    public void The_hq_gives_a_transport_every_10_war_minutes()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        while (run.War.Clock < 599.0) run.War.Step();
        Assert.Equal(5, run.Transport);
        TfwRig.Seconds(w, 8f);
        Assert.Equal(6, run.Transport);
    }

    [Fact]
    public void A_new_battle_clears_the_guardsmen_of_the_last_and_the_drops()
    {
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        Assert.NotNull(w.MusouSpawnDrop(Human, TfwTestArena.LandingZone, 0));
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.3f);
        Assert.Equal(0, w.TfwGuards!.Alive);
        Assert.DoesNotContain(w.MusouDrops, d => d.State == MusouDropState.Live);
        TfwRig.Deploy(w);
        Assert.Equal(60, w.TfwGuards.Alive + w.TfwGuards.Lost);   // a Marine landing among them may shove one to death (M5): he is still of this battle
    }

    [Fact]
    public void The_war_keeps_time_by_the_scale_and_freezes_the_battles_lane()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        double clock = run.War.Clock;
        TfwRig.Seconds(w, 8f);
        Assert.Equal(2.0, run.War.Clock - clock, 1);                         // the hub: a quarter
        TfwRig.Deploy(w);
        var lane = run.War.LaneState(TfwLane.A);
        clock = run.War.Clock;
        TfwRig.Seconds(w, 10f);
        Assert.Equal(10.0, run.War.Clock - clock, 1);
        Assert.Equal(lane.Progress, run.War.LaneState(TfwLane.A).Progress);
        Assert.Equal(1f, run.WarTimeScale);
    }

    static (ulong Hash, int Tick) WholeRun()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        TfwRig.WinBattle(w, TfwLane.A);
        TfwRig.WinBattle(w, TfwLane.A);
        Assert.Equal(TfwLane.A, run.War.FinalLane);

        var battle = TfwRig.Deploy(w);
        Assert.Equal(TfwRunPhase.FinalAssault, run.Phase);
        Assert.True(w.Musou!.TryGet(battle.BossId, out var boss));
        Assert.Equal(MusouEnemyType.Warboss, boss.Type);
        var bonus = run.War.FinalBonus(TfwLane.A);
        Assert.Equal(w.Musou.Rules.BossHealth * bonus.BossHealthFactor, battle.BossMaxHealth);
        Assert.Equal(TfwBattleRules.SquadsAt(run.War.LaneState(TfwLane.A).Imperial) + bonus.ExtraSquads, battle.Squads);
        double clock = run.War.Clock;
        TfwRig.Seconds(w, 5f);
        Assert.Equal(clock, run.War.Clock);                                  // the war waits for the assault

        TfwRig.KillAll(w);
        TfwRig.Steps(w, 2);
        Assert.Equal(TfwRunPhase.Won, run.Phase);
        Assert.Equal(MatchState.Ended, w.State);
        Assert.Equal(Team.SpaceMarines, w.Winner);
        return (run.War.StateHash(), (int)w.Tick);
    }

    [Fact]
    public void A_whole_scripted_run_reaches_the_final_assault_and_wins_the_same_way_twice() => Assert.Equal(WholeRun(), WholeRun());

    [Fact]
    public void Without_guardsmen_the_wire_carries_the_war_and_no_guardsman()
    {
        var map = TfwTestArena.ServerMap();
        var h = new Harness(LinkConditions.Perfect, 5, 1, TfwRig.Config, new SmServer.ServerOptions { Match = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } }, map: map,
            clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        h.Run(TimeSpan.FromSeconds(1));
        var run = h.Server.World.Tfw!;
        run.Guardsmen = false;
        Assert.True(run.Enqueue(h.Clients[0].PlayerId, new TfwCommandRecord(1, TfwCommand.Deploy, 0)));
        h.Clients[0].TakeExtras();
        h.Run(TimeSpan.FromSeconds(TfwBattleRules.TravelSeconds + 1f));
        Assert.Equal(TfwRunPhase.Battle, run.Phase);
        Assert.Equal(0, h.Server.TfwFeed.GuardCount);
        var tags = h.Clients[0].TakeExtras().Select(s => s.Tag).ToHashSet();
        Assert.Contains(TfwWire.WarTag, tags);
        Assert.DoesNotContain(TfwWire.GuardsTag, tags);
    }

    [Fact]
    public void The_server_feeds_the_wire_from_the_run_each_guardsman_once()
    {
        var map = TfwTestArena.ServerMap();
        var h = new Harness(LinkConditions.Perfect, 5, 1, TfwRig.Config, new SmServer.ServerOptions { Match = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } }, map: map,
            clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        h.Run(TimeSpan.FromSeconds(1));
        var run = h.Server.World.Tfw!;
        Assert.Same(run.War, h.Server.TfwFeed.War);                          // set from the hub on
        run.Guardsmen = true;
        Assert.True(run.Enqueue(h.Clients[0].PlayerId, new TfwCommandRecord(1, TfwCommand.Deploy, 0)));
        h.Clients[0].TakeExtras();
        h.Run(TimeSpan.FromSeconds(TfwBattleRules.TravelSeconds + 1f));
        Assert.Equal(TfwRunPhase.Battle, run.Phase);
        var feed = h.Server.TfwFeed;
        Assert.Equal(run.Objectives.Count, feed.Objectives.Count);
        Assert.Equal(60, feed.GuardCount);
        Assert.Equal(60, feed.Guards.Take(feed.GuardCount).Select(g => g.Id).Distinct().Count());
        Assert.Equal(4, feed.Transport);
        var tags = h.Clients[0].TakeExtras().Select(s => s.Tag).ToHashSet();
        Assert.Contains(TfwWire.WarTag, tags);
        Assert.Contains(TfwWire.ObjectivesTag, tags);
        Assert.Contains(TfwWire.GuardsTag, tags);
    }

    [Fact]
    public void A_position_is_secured_on_the_real_walking_graph()
    {
        var map = TfwTestArena.ServerMap();
        var h = new Harness(LinkConditions.Perfect, 5, 0, TfwRig.Config, new SmServer.ServerOptions { Match = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } }, map: map);
        var nav = Assert.IsType<NavGraph>(h.Server.World.Musou!.Nav);
        var w = TfwRig.War(nav);
        var battle = TfwRig.Deploy(w);
        Assert.Equal(TfwTestArena.StartAlive, w.Musou!.Alive);
        TfwRig.Secure(w, battle, 0);
        TfwRig.Secure(w, battle, 1);
        Assert.Equal((6, 5), (w.Tfw!.Transport, w.Tfw.Supplies));
    }

    [Fact]
    public void Every_human_gone_sends_the_war_back_to_warmup_with_a_new_run()
    {
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        w.Remove(Human);
        TfwRig.Steps(w, 2);
        Assert.Equal(MatchState.Warmup, w.State);
        Assert.Equal(0, w.Musou!.Count);
        Assert.Equal(5, w.Tfw!.Transport);
    }
}
