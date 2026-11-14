using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// charge-responsive (protocol 69; the owner 2026-11-12): a charge (Shift + W + F) starts as soon as the melee attack under way has hit (a light attack, a combo, a heavy strike) instead of waiting for
/// the attack's whole clip; a swing starts at once after a charge ends or is cancelled; S (the stick held back firmly) cancels the bull rush from the tick after its start, not from its stick branch.
/// Restore switches <see cref="SimConfig.ChargeAfterMelee"/> and <see cref="SimConfig.ChargeInstantCancel"/> (false) bring the earlier rules back. Synthetic clips (<see cref="RushTests"/>): the light attack
/// lasts 1 s with its hit at 0.2 s; the rush moves 10 m/s and ends at 1.0 s.
/// </summary>
public class ChargeResponsiveTests(ITestOutputHelper output)
{
    static readonly SimConfig Config = RushTests.Config;
    static readonly SimConfig OldMelee = Config with { ChargeAfterMelee = false };
    static readonly SimConfig OldCancel = Config with { ChargeInstantCancel = false };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c);
    static PlayerInput Press(InputButtons b, float y = 0f, float x = 0f) => new(0, x, y, 0f, b);
    static readonly PlayerInput Idle = Press(InputButtons.None);
    static readonly PlayerInput Charge = Press(InputButtons.Sprint | InputButtons.MeleeHeavy, 1f);
    static PlayerState Step(PlayerState s, PlayerInput i, SimConfig c) => Combat.StepPlayer(s, i, c, out _);

    /// <summary>A light attack pressed at tick 0, then <paramref name="idle"/> idle ticks.</summary>
    static PlayerState Swinging(SimConfig c, int idle)
    {
        var s = Step(Fresh(c), Press(InputButtons.Melee), c);
        Assert.Equal(1, s.MeleeStep);
        for (int k = 0; k < idle; k++) s = Step(s, Idle, c);
        return s;
    }

    // ---------------------------------------------------------------- charge after a swing

    [Fact]
    public void A_charge_pressed_after_a_light_attack_starts_on_the_first_tick_its_hit_has_landed()
    {
        var light = Config.Classes[0].Melee![0];
        int first = -1;
        for (int idle = 0; idle < 29; idle++)
        {
            var before = Swinging(Config, idle);
            if (before.MeleeStep == 0) break;
            bool landed = before.MeleeTime + 1e-5f >= light.HitTime;
            var after = Step(before, Charge, Config);
            Assert.Equal(landed, after.Rush == RushPhase.Rush);   // the rule, at every tick of the attack
            if (landed && first < 0) first = idle;
            if (landed)
            {
                Assert.Equal((RushPhase.Rush, 1, 0, false, 0f), (after.Rush, after.RushTicks, after.MeleeStep, after.MeleeQueued, after.MeleeLock));   // the attack and its wait are over
                Assert.Equal(RushPhase.None, before.Rush);
            }
        }
        output.WriteLine($"first legal tick: {first} idle ticks after the swing's start (hit at {light.HitTime:F2} s)");
        Assert.InRange(first, 1, 8);   // the 0.2 s hit: a handful of ticks, not the attack's 30
    }

    [Fact]
    public void A_heavy_strike_can_be_charged_from_after_its_hit_but_not_before()
    {
        var heavy = Step(Fresh(Config), Press(InputButtons.MeleeHeavy), Config);   // no W: heavy strike 1 (hit at 0.3 s)
        Assert.Equal((1, true), (heavy.MeleeStep, heavy.MeleeHeavy));
        var early = heavy;
        for (int k = 0; k < 4; k++) early = Step(early, Idle, Config);
        Assert.NotEqual(RushPhase.Rush, Step(early, Charge, Config).Rush);   // before the hit: not yet
        var hit = heavy;
        for (int k = 0; k < 10; k++) hit = Step(hit, Idle, Config);
        Assert.True(hit.MeleeStep > 0 && hit.MeleeTime >= 0.3f);
        var charged = Step(hit, Charge, Config);
        Assert.Equal((RushPhase.Rush, 0, false), (charged.Rush, charged.MeleeStep, charged.MeleeHeavy));
    }

    [Fact]
    public void A_charge_can_follow_a_queued_next_attack_of_a_combo()
    {
        // Attack 1, a second press queues attack 2, the charge press after attack 1's hit ends the combo (the queued attack is dropped).
        var s = Swinging(Config, 3);
        s = Step(s, Press(InputButtons.Melee), Config);
        Assert.True(s.MeleeQueued || s.MeleeStep > 0);
        for (int k = 0; k < 3; k++) s = Step(s, Idle, Config);
        var charged = Step(s, Charge, Config);
        Assert.Equal((RushPhase.Rush, 0, false), (charged.Rush, charged.MeleeStep, charged.MeleeQueued));
    }

    [Fact]
    public void The_restore_switch_makes_the_charge_wait_for_the_swing_to_end()
    {
        var s = Swinging(OldMelee, 12);
        var after = Step(s, Charge, OldMelee);
        Assert.Equal((RushPhase.None, true), (after.Rush, after.MeleeStep > 0));   // the swing goes on, nothing was thrown away
        Assert.Equal(RushPhase.Rush, Step(Swinging(Config, 12), Charge, Config).Rush);
    }

    [Fact]
    public void A_refused_charge_keeps_the_swing_the_spacing_the_W_and_the_ground_rules_still_apply()
    {
        var spaced = Step(Swinging(Config, 12) with { RushLock = 5 }, Charge, Config);   // the last charge's 1 s has not passed
        Assert.Equal((RushPhase.None, true), (spaced.Rush, spaced.MeleeStep > 0));
        Assert.NotEqual(RushPhase.Rush, Step(Swinging(Config, 12), Press(InputButtons.Sprint | InputButtons.MeleeHeavy, 0f), Config).Rush);   // without W: not a charge
        Assert.NotEqual(RushPhase.Rush, Step(Swinging(Config, 12) with { Grounded = false }, Charge, Config).Rush);                            // in the air: refused
    }

    [Fact]
    public void A_press_a_little_before_the_hit_is_kept_by_the_input_buffer_and_charges_at_the_hit()
    {
        var c = Config with { InputBufferWindow = 0.3f };
        var s = Step(Swinging(c, 1), Charge, c);   // too early: not a charge yet
        Assert.NotEqual(RushPhase.Rush, s.Rush);
        for (int k = 0; k < 8 && s.Rush != RushPhase.Rush; k++) s = Step(s, Press(InputButtons.Sprint, 1f), c);
        Assert.Equal(RushPhase.Rush, s.Rush);
    }

    // ---------------------------------------------------------------- a swing after a charge

    [Fact]
    public void A_swing_starts_on_the_first_tick_after_a_charge_ends_or_is_cancelled()
    {
        var s = Step(Fresh(Config), Charge, Config);   // cancelled by S at its 6th tick, then a light press at once
        for (int k = 0; k < 5; k++) s = Step(s, Idle, Config);
        s = Step(s, Press(InputButtons.None, -1f), Config);
        Assert.Equal(RushPhase.None, s.Rush);
        Assert.Equal(1, Step(s, Press(InputButtons.Melee), Config).MeleeStep);
        var t = Step(Fresh(Config), Charge, Config);   // ended by its clip's end
        while (t.Rush == RushPhase.Rush) t = Step(t, Idle, Config);
        Assert.Equal(1, Step(t, Press(InputButtons.Melee), Config).MeleeStep);
        var u = Step(Swinging(Config, 7), Charge, Config);   // a swing that charged: the melee wait the attack set is gone
        Assert.Equal(0f, u.MeleeLock);
        for (int k = 0; k < 5; k++) u = Step(u, Idle, Config);
        u = Step(u, Press(InputButtons.None, -1f), Config);
        Assert.Equal((RushPhase.None, 1), (u.Rush, Step(u, Press(InputButtons.Melee), Config).MeleeStep));
    }

    // ---------------------------------------------------------------- the S cancel

    [Fact]
    public void S_cancels_the_rush_on_the_first_tick_it_is_pressed_from_the_second_tick_of_the_charge_on()
    {
        int tested = 0;
        for (int tap = 1; tap < 30; tap++)
        {
            var s = Step(Fresh(Config), Charge, Config);
            for (int k = 1; k < tap; k++) s = Step(s, Idle, Config);
            Assert.Equal(RushPhase.Rush, s.Rush);
            var cancelled = Step(s, Press(InputButtons.None, -1f), Config);
            Assert.Equal((RushPhase.None, ReactionKind.None), (cancelled.Rush, cancelled.Reaction));   // at once, whatever the tick
            Assert.True(cancelled.RushLock > 0, "the spacing runs on");
            float speed = new Vector2(cancelled.Velocity.X, cancelled.Velocity.Y).Length();
            Assert.True(speed <= Config.SprintSpeed + 0.01f, $"tick {tap}: the speed carried on ({speed:F1} m/s) is capped at the sprint's");
            tested++;
        }
        output.WriteLine($"cancelled at every one of {tested} ticks");
    }

    [Theory]
    [InlineData(0f, -1f)]
    [InlineData(-1f, -1f)]
    [InlineData(1f, -1f)]
    [InlineData(0.2f, -0.6f)]
    public void S_with_A_or_D_and_a_pad_stick_pulled_back_cancel_too(float x, float y)
    {
        var s = Step(Step(Fresh(Config), Charge, Config), Idle, Config);
        Assert.Equal(RushPhase.None, Step(s, Press(InputButtons.None, y, x), Config).Rush);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0f, 1f)]
    [InlineData(1f, 0f)]
    [InlineData(-1f, 0f)]
    [InlineData(0f, -0.3f)]   // a stick not pulled back firmly (below ChargeCancel.InstantBackStick): the branch's rule only
    [InlineData(1f, -0.3f)]
    public void Other_sticks_and_a_weak_back_pull_do_not_cancel_early(float x, float y)
    {
        var s = Step(Fresh(Config), Charge, Config);
        for (int k = 0; k < 5; k++) s = Step(s, Press(InputButtons.None, y, x), Config);
        Assert.Equal(RushPhase.Rush, s.Rush);
    }

    [Fact]
    public void S_held_when_the_charge_starts_cannot_cancel_it_because_the_start_needs_W()
    {
        // The start needs the stick's forward part (W), so S is never held at the press tick (a keyboard's W + S cancel out to no forward part: no charge at all).
        Assert.NotEqual(RushPhase.Rush, Step(Fresh(Config), Press(InputButtons.Sprint | InputButtons.MeleeHeavy, -1f), Config).Rush);
        Assert.NotEqual(RushPhase.Rush, Step(Fresh(Config), Press(InputButtons.Sprint | InputButtons.MeleeHeavy, 0f), Config).Rush);
        // Stated rule: no fresh press is needed after the start; S from the second tick cancels.
        var started = Step(Fresh(Config), Charge, Config);
        Assert.Equal(RushPhase.None, Step(started, Press(InputButtons.None, -1f), Config).Rush);
    }

    [Fact]
    public void The_restore_switch_brings_back_the_wait_for_the_stick_branch()
    {
        var s = Step(Fresh(OldCancel), Charge, OldCancel);
        int ticks = 1;
        while (s.Rush == RushPhase.Rush) { s = Step(s, Press(InputButtons.None, -1f), OldCancel); ticks++; }
        Assert.Equal(1 + 18, ticks);   // RushTests' rush: its stick branch at 0.6 s
    }

    [Fact]
    public void The_switches_are_in_the_config_hash_only_when_off_and_on_the_command_line()
    {
        Assert.Equal(Config.Hash(), (Config with { ChargeAfterMelee = true, ChargeInstantCancel = true }).Hash());
        Assert.NotEqual(Config.Hash(), OldMelee.Hash());
        Assert.NotEqual(Config.Hash(), OldCancel.Hash());
        Assert.NotEqual(OldMelee.Hash(), OldCancel.Hash());
        Assert.True(ServerCommandLine.TryParse(["--old-charge-after-melee", "--old-charge-cancel-delay"], out var a, out var error), error);
        Assert.Equal((true, true), (a.OldChargeAfterMelee, a.OldChargeCancelDelay));
        Assert.True(ServerCommandLine.TryParse([], out var none, out _));
        Assert.Equal((false, false), (none.OldChargeAfterMelee, none.OldChargeCancelDelay));
        Assert.Equal(73, Protocol.Version);
    }

    // ---------------------------------------------------------------- the world and prediction

    [Fact]
    public void The_world_charges_after_a_swing_and_cancels_with_S()
    {
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f)]);
        w.Spawn(1, 0);
        var states = new List<PlayerState>();
        for (int k = 0; k < 40; k++)
        {
            uint view = w.Tick;
            var input = k == 0 ? Press(InputButtons.Melee) : k == 10 ? Charge : k == 16 ? Press(InputButtons.None, -1f) : k == 18 ? Press(InputButtons.Melee) : Idle;
            w.Step(_ => input with { ViewTick = view });
            states.Add(w.Players[1]);
        }
        Assert.Equal(RushPhase.Rush, states[10].Rush);
        Assert.Equal(RushPhase.Rush, states[15].Rush);
        Assert.Equal(RushPhase.None, states[16].Rush);
        Assert.Equal(1, states[18].MeleeStep);
    }

    [Theory]
    [InlineData(411)]
    [InlineData(412)]
    public void Prediction_matches_the_server_through_swing_charge_cancel_and_swing_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, Config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5), SnapshotRate = 20, Match = new MatchRules { TimeLimit = 0f, CountdownTime = 0f } });
        h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = 0, SmoothInputLead = false });
        var truth = new Dictionary<uint, PlayerState>();
        var predicted = new Dictionary<uint, PlayerState>();
        int charges = 0, early = 0;
        PlayerInput Input(int i, uint t)
        {
            uint p = t % 90;
            bool cancelRound = t / 90 % 2 == 0;
            if (p == 0) return new PlayerInput(t, 0f, 0f, 0f, InputButtons.Melee);                                              // a swing
            if (p == 9) return new PlayerInput(t, 0f, 1f, 0f, InputButtons.Sprint | InputButtons.MeleeHeavy);                  // charge from it
            if (p is >= 10 and < 13) return new PlayerInput(t, 0f, 1f, 0f, InputButtons.None);
            if (p == 13 && cancelRound) return new PlayerInput(t, 0f, -1f, 0f, InputButtons.None);                              // S cancel, 4 ticks in
            if (p == 14 && cancelRound) return new PlayerInput(t, 0f, 0f, 0f, InputButtons.Melee);                              // then a swing at once
            return new PlayerInput(t, 0f, 0f, 0f, InputButtons.None);
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            var w = h.Server.World;
            if (!truth.ContainsKey(w.Tick) && w.Players.TryGetValue(h.Clients[0].PlayerId, out var me))
            {
                if (truth.TryGetValue(w.Tick - 1, out var last) && last.Rush == RushPhase.None && me.Rush == RushPhase.Rush) charges++;
                if (truth.TryGetValue(w.Tick - 1, out var l2) && l2.Rush == RushPhase.Rush && me.Rush == RushPhase.None && l2.RushTicks < 15) early++;
                truth[w.Tick] = me;
            }
            if (h.Clients[0].Status == ClientStatus.Connected) predicted[h.Clients[0].Tick] = h.Clients[0].Predicted;
        });
        int compared = 0; var mismatches = new List<string>();
        uint from = truth.Keys.First() + 90;
        foreach (var (tick, pred) in predicted)
            if (tick >= from && truth.TryGetValue(tick, out var real)) { compared++; if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}"); }
        output.WriteLine($"compared {compared} ticks, {mismatches.Count} mismatches, {charges} charges, {early} early S cancels");
        foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
        Assert.True(compared > 600, $"only {compared} compared");
        Assert.True(charges >= 6 && early >= 3, $"charges {charges}, early cancels {early}");
        Assert.Empty(mismatches);
    }
}
