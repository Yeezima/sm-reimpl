using System.Numerics;
using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// bot-grenade-safety (owner, 2026-11-11; the rules are ours): the Classic PvP bots' throw rule (3D distance, the predicted landing point, the safety distance per item from its
/// own blast radius), the hold near an own live grenade (no move, jump, dash, hop or charge into it; backing out from inside), the remote-detonation rule, the restore flag and
/// Musou Mode's co-op bots left alone; and, in the simulation, the old aim's self-kill against the new aim's safe throw. Synthetic items (GrenadeTests' frag-like shapes).
/// </summary>
public class BotGrenadeSafetyTests(ITestOutputHelper output)
{
    static readonly ThrownSpec Frag = GrenadeTests.Frag;                 // blast r 10, 200 in steps
    static readonly ThrownSpec BigBlast = GrenadeTests.Krak;             // a damaging blast of r 30 (the synthetic "krak" of GrenadeTests)
    static readonly ThrownSpec Bomb = GrenadeTests.Bomb;                 // a lingering r 8 blast then a burst of r 6
    static readonly ThrownSpec Blind = new("test_blind", 2, 1, 1f, 10f, GrenadeTests.FragBall with
    {
        Splash = new SplashSpec(8f, new DamageSpec(0f, 0f, 1f, 1f, 1f, 1f, false), [], true, false, "target_all") { BlindOnly = true, Blindness = 3.5f },
    });
    static readonly ThrownSpec Stims = new("test_stims", 1, -1, 6f, 0f, ThrownSpec.NoProjectile) { Stim = new StimSpec(5f, 0.3f, 0.9f) };
    const float Chest = 1.2f;

    static Vector3 At(float x, float y, float z = 0f) => new(x, y, z);
    static Vector3 ChestAt(float x, float y, float z = 0f) => new(x, y, z + Chest);

    // ---------------------------------------------------------------- the safety distance per item

    [Fact]
    public void The_safety_distance_is_the_items_own_damaging_blast_radius_plus_the_margin()
    {
        Assert.Equal(10f + BotGrenadeSafety.Margin, BotGrenadeSafety.SafetyDistance(Frag), 3);
        Assert.Equal(30f + BotGrenadeSafety.Margin, BotGrenadeSafety.SafetyDistance(BigBlast), 3);
        Assert.Equal(8f + BotGrenadeSafety.Margin, BotGrenadeSafety.SafetyDistance(Bomb), 3);   // the larger of its two damaging blasts
        Assert.Equal(20f + BotGrenadeSafety.Margin, BotGrenadeSafety.SafetyDistance(Frag, reach: 2f), 3);   // a blast reach scale (Musou's doubled frag) scales the radius
        Assert.Equal(1.5f, BotGrenadeSafety.Margin);
    }

    [Fact]
    public void Stims_the_blind_grenade_and_items_without_a_damaging_blast_keep_the_old_rule()
    {
        Assert.Equal(0f, BotGrenadeSafety.SafetyDistance(Blind));
        Assert.Equal(0f, BotGrenadeSafety.SafetyDistance(Stims));
        Assert.Equal(0f, BotGrenadeSafety.SafetyDistance((ThrownSpec?)null));
        Assert.Equal(0f, BotGrenadeSafety.SafetyDistance(GrenadeTests.FragBall with { Splash = null }));
        // The old rule: 6 m horizontally (stims at any distance), at most the bot's longest throw.
        Assert.False(BotGrenadeSafety.OldThrowAllowed(5.9f, stim: false, 20f));
        Assert.True(BotGrenadeSafety.OldThrowAllowed(6f, stim: false, 20f));
        Assert.True(BotGrenadeSafety.OldThrowAllowed(1f, stim: true, 20f));
        Assert.False(BotGrenadeSafety.OldThrowAllowed(20.1f, stim: true, 20f));
        Assert.Equal(2f, BotGrenadeSafety.OldAimDistance(7f));    // the old aim: 8 m short, at least 2 m in front
        Assert.Equal(6f, BotGrenadeSafety.OldAimDistance(14f));
    }

    // ---------------------------------------------------------------- the throw rule

    [Theory]
    [InlineData(8f, 0f, BotGrenadeSafety.ThrowVerdict.TargetTooClose)]    // the old rule threw at 6 m and more horizontally
    [InlineData(11f, 0f, BotGrenadeSafety.ThrowVerdict.TargetTooClose)]
    [InlineData(12f, 0f, BotGrenadeSafety.ThrowVerdict.Throw)]
    [InlineData(9f, 7f, BotGrenadeSafety.ThrowVerdict.TargetTooClose)]    // 3D: 9 m out and 7 m up is 11.4 m (the horizontal rule threw)
    [InlineData(20f, 0f, BotGrenadeSafety.ThrowVerdict.Throw)]
    [InlineData(20.5f, 0f, BotGrenadeSafety.ThrowVerdict.OutOfRange)]
    public void The_target_must_be_beyond_the_safety_distance_in_3D(float horizontal, float above, BotGrenadeSafety.ThrowVerdict expected)
    {
        float safety = BotGrenadeSafety.SafetyDistance(Frag);
        var target = At(0f, horizontal, above);
        var landing = BotGrenadeSafety.PredictLanding(target, Vector3.Zero, 0.4f);
        Assert.Equal(expected, BotGrenadeSafety.CheckThrow(ChestAt(0, 0), target + new Vector3(0, 0, Chest), landing, safety, 20f));
    }

    [Fact]
    public void A_target_far_below_is_a_throw_that_the_horizontal_rule_refused()
    {
        float safety = BotGrenadeSafety.SafetyDistance(Frag);
        var target = At(3f, 0f, -14f);   // 3 m out, 14 m below (the bot on a balcony)
        Assert.False(BotGrenadeSafety.OldThrowAllowed(3f, stim: false, 20f));
        Assert.Equal(BotGrenadeSafety.ThrowVerdict.Throw, BotGrenadeSafety.CheckThrow(ChestAt(0, 0), target + new Vector3(0, 0, Chest), target, safety, 20f));
    }

    [Fact]
    public void A_target_closing_in_is_refused_by_its_predicted_landing_point()
    {
        float safety = BotGrenadeSafety.SafetyDistance(Frag);
        var target = At(0f, 14f);
        var running = new Vector3(0f, -7f, 3f);   // at the bot at 7 m/s (a jump's rise does not lift the point)
        float flight = 0.2f + BotGrenadeSafety.FlightSeconds(14f, Frag.Projectile.Speed);   // the release delay and the flight
        var landing = BotGrenadeSafety.PredictLanding(target, running, flight);
        Assert.Equal(0f, landing.Z);
        Assert.Equal(14f - 7f * flight, landing.Y, 3);
        Assert.Equal(BotGrenadeSafety.ThrowVerdict.LandsTooClose, BotGrenadeSafety.CheckThrow(ChestAt(0, 0), target + new Vector3(0, 0, Chest), landing, safety, 20f));
        // Standing still, the same target is a throw.
        Assert.Equal(BotGrenadeSafety.ThrowVerdict.Throw, BotGrenadeSafety.CheckThrow(ChestAt(0, 0), target + new Vector3(0, 0, Chest), BotGrenadeSafety.PredictLanding(target, Vector3.Zero, flight), safety, 20f));
    }

    [Fact]
    public void A_target_in_the_air_is_not_thrown_at()
    {
        float safety = BotGrenadeSafety.SafetyDistance(Frag);
        Assert.True(BotGrenadeSafety.Airborne(JumpPhase.Ascent, Vector3.Zero));
        Assert.True(BotGrenadeSafety.Airborne(JumpPhase.TakeOff, Vector3.Zero));
        Assert.True(BotGrenadeSafety.Airborne(JumpPhase.Dive, Vector3.Zero));
        Assert.True(BotGrenadeSafety.Airborne(JumpPhase.None, new Vector3(0f, 0f, -5f)));   // a fall or a plain jump
        Assert.False(BotGrenadeSafety.Airborne(JumpPhase.None, new Vector3(6f, 0f, 0f)));   // running on the ground
        Assert.False(BotGrenadeSafety.Airborne(JumpPhase.Landed, new Vector3(0f, 0f, -1f)));
        var target = At(0f, 15f, 8f);   // a jump-pack flier 15 m out, 8 m up
        Assert.Equal(BotGrenadeSafety.ThrowVerdict.TargetInAir, BotGrenadeSafety.CheckThrow(ChestAt(0, 0), target + new Vector3(0, 0, Chest), target, safety, 20f, targetAirborne: true));
        // Nor from the air: the bot moves on between the press and the release.
        Assert.Equal(BotGrenadeSafety.ThrowVerdict.OwnInAir, BotGrenadeSafety.CheckThrow(ChestAt(0, 0, 5f), ChestAt(0, 15f), At(0, 15f), safety, 20f, ownAirborne: true));
        // Too close is still said first (the log names the nearer danger).
        Assert.Equal(BotGrenadeSafety.ThrowVerdict.TargetTooClose, BotGrenadeSafety.CheckThrow(ChestAt(0, 0), ChestAt(0, 5f, 4f), At(0, 5f, 4f), safety, 20f, targetAirborne: true));
    }

    [Fact]
    public void A_landing_point_cut_short_by_the_level_is_refused()
    {
        // The client moves the landing to the first level contact on the aimed line (a ledge's edge below a target above): 4 m out.
        float safety = BotGrenadeSafety.SafetyDistance(Frag);
        Assert.Equal(BotGrenadeSafety.ThrowVerdict.LandsTooClose, BotGrenadeSafety.CheckThrow(ChestAt(0, 0), ChestAt(0, 15f, 6f), At(0f, 4f, 3f), safety, 20f));
    }

    [Fact]
    public void A_bigger_blast_needs_a_farther_target()
    {
        var target = At(0f, 15f);
        var landing = target;
        Assert.Equal(BotGrenadeSafety.ThrowVerdict.Throw, BotGrenadeSafety.CheckThrow(ChestAt(0, 0), target + new Vector3(0, 0, Chest), landing, BotGrenadeSafety.SafetyDistance(Frag), 20f));
        Assert.Equal(BotGrenadeSafety.ThrowVerdict.TargetTooClose, BotGrenadeSafety.CheckThrow(ChestAt(0, 0), target + new Vector3(0, 0, Chest), landing, BotGrenadeSafety.SafetyDistance(Frag, reach: 2f), 20f));
        Assert.Equal(BotGrenadeSafety.ThrowVerdict.TargetTooClose, BotGrenadeSafety.CheckThrow(ChestAt(0, 0), target + new Vector3(0, 0, Chest), landing, BotGrenadeSafety.SafetyDistance(BigBlast), 40f));
    }

    // ---------------------------------------------------------------- the hold near an own live grenade

    static readonly BotGrenadeSafety.Hazard[] FragAhead = [new(At(0f, 14f), BotGrenadeSafety.SafetyDistance(Frag))];   // 14 m ahead (yaw 0 faces +Y), reach 11.5 m

    static PlayerInput Input(float x, float y, InputButtons buttons, float yaw = 0f) => new(7, x, y, yaw, buttons, -0.1f);

    [Fact]
    public void Without_a_hazard_the_input_is_unchanged()
    {
        var input = Input(0f, 1f, InputButtons.Jump | InputButtons.Sprint | InputButtons.MeleeHeavy);
        Assert.Equal(input, BotGrenadeSafety.Hold(input, At(0, 0), ChestAt(0, 0), [], out var kind));
        Assert.Equal(BotGrenadeSafety.HoldKind.None, kind);
    }

    [Theory]
    [InlineData(InputButtons.None)]                                         // walking at it
    [InlineData(InputButtons.Sprint)]                                       // sprinting at it
    [InlineData(InputButtons.Jump)]                                         // a jump, dash or hop at it
    [InlineData(InputButtons.Sprint | InputButtons.MeleeHeavy)]             // the charge (W + Shift + F)
    public void A_move_jump_or_charge_into_the_blast_is_held(InputButtons buttons)
    {
        var at = At(0f, 0f);
        var input = Input(0f, 1f, buttons | InputButtons.Fire);
        var held = BotGrenadeSafety.Hold(input, at, ChestAt(0, 0), FragAhead, out var kind);
        Assert.Equal(BotGrenadeSafety.HoldKind.Held, kind);
        Assert.Equal((0f, 0f), (held.MoveX, held.MoveY));
        Assert.Equal(InputButtons.None, held.Buttons & (InputButtons.Jump | InputButtons.Sprint | InputButtons.MeleeHeavy));
        Assert.True((held.Buttons & InputButtons.Fire) != 0);   // it keeps shooting
        Assert.Equal((input.Yaw, input.Pitch), (held.Yaw, held.Pitch));
    }

    [Fact]
    public void A_jump_or_charge_with_no_stick_goes_where_the_bot_faces()
    {
        var facing = BotGrenadeSafety.Hold(Input(0f, 0f, InputButtons.Jump), At(0, 0), ChestAt(0, 0), FragAhead, out var kind);
        Assert.Equal(BotGrenadeSafety.HoldKind.Held, kind);
        Assert.Equal(InputButtons.None, facing.Buttons & InputButtons.Jump);
        var away = Input(0f, 0f, InputButtons.Jump, yaw: MathF.PI);   // facing away from the grenade
        Assert.Equal(away, BotGrenadeSafety.Hold(away, At(0, 0), ChestAt(0, 0), FragAhead, out kind));
        Assert.Equal(BotGrenadeSafety.HoldKind.None, kind);
    }

    [Theory]
    [InlineData(0f, -1f)]   // backing off
    [InlineData(1f, 0f)]    // strafing across
    [InlineData(-1f, 0f)]
    public void A_move_away_or_across_stays(float x, float y)
    {
        var input = Input(x, y, InputButtons.Fire | InputButtons.Jump);
        Assert.Equal(input, BotGrenadeSafety.Hold(input, At(0, 0), ChestAt(0, 0), FragAhead, out var kind));
        Assert.Equal(BotGrenadeSafety.HoldKind.None, kind);
    }

    [Fact]
    public void Inside_the_blast_the_bot_backs_straight_out_without_jump_melee_or_charge()
    {
        var hazards = new BotGrenadeSafety.Hazard[] { new(At(3f, 4f), BotGrenadeSafety.SafetyDistance(Frag)) };   // 5 m away, ahead and to the right
        float yaw = 0.7f;
        var input = Input(0f, 1f, InputButtons.Fire | InputButtons.Melee | InputButtons.MeleeHeavy | InputButtons.Sprint | InputButtons.Jump, yaw);
        var out1 = BotGrenadeSafety.Hold(input, At(0, 0), ChestAt(0, 0), hazards, out var kind);
        Assert.Equal(BotGrenadeSafety.HoldKind.BackedOut, kind);
        var move = BotGrenadeSafety.WorldMove(out1);
        Assert.Equal(-0.6f, move.X, 3);   // straight away from the grenade: (0,0) - (3,4), normalised
        Assert.Equal(-0.8f, move.Y, 3);
        Assert.Equal(InputButtons.Fire, out1.Buttons);
        Assert.Equal(yaw, out1.Yaw);      // the view (and aim) stays on the target
    }

    [Fact]
    public void A_charge_that_runs_through_the_blast_crosses_it_one_that_passes_by_does_not()
    {
        var hazards = FragAhead;
        Assert.True(BotGrenadeSafety.PathCrosses(hazards, ChestAt(0, 0), ChestAt(0, 30f)));    // through it to a target beyond
        Assert.False(BotGrenadeSafety.PathCrosses(hazards, ChestAt(0, 0), ChestAt(-12f, 0f)));  // sideways, never nearer than 14 m
        Assert.True(BotGrenadeSafety.Inside(hazards, ChestAt(0, 4f)));
        Assert.False(BotGrenadeSafety.Inside(hazards, ChestAt(0, 2f)));   // 12 m from it (chest 1.2 m up): outside the 11.5 m reach
    }

    [Fact]
    public void Only_a_grenade_that_could_have_left_the_hand_by_now_is_claimed_as_the_bots_own()
    {
        float speed = Frag.Projectile.Speed;
        Assert.True(BotGrenadeSafety.Claimable(2f, 0f, speed));        // at the hand on the first snapshot
        Assert.True(BotGrenadeSafety.Claimable(7f, 0.1f, speed));       // 4.75 m of flight + slack
        Assert.False(BotGrenadeSafety.Claimable(16f, 0f, speed));       // an enemy's grenade landing 16 m out at the release (measured in a match)
        Assert.False(BotGrenadeSafety.Claimable(16f, 0.2f, speed));     // 9.5 m of flight + 3 m at most
        Assert.False(BotGrenadeSafety.Claimable(5f, 1.2f, speed));      // too late to be claimed at all
    }

    // ---------------------------------------------------------------- self-splash shots

    /// <summary>Plasma-cannon-like: a ball that bursts on contact, blast r 7 (60, ×1 to 3.5 m).</summary>
    static readonly ProjectileSpec PlasmaBall = new("ebps\\test\\plasma", 100f, 3f, "raycast", 0.1f, [new(20.1f, 0.25f)], true, false, false, 0, 0f, 0f, 0, null,
        new SplashSpec(7f, new DamageSpec(60f, 0f, 1f, 1f, 2f, 2f, true), [new(0f, 1f), new(3.5f, 1f), new(7f, 0.15f)], true, false, "target_all"));

    [Fact]
    public void A_splash_shot_is_not_fired_where_it_would_burst_within_its_own_blast_of_the_bot()
    {
        Assert.Equal(7f + BotGrenadeSafety.Margin, BotGrenadeSafety.SafetyDistance(PlasmaBall), 3);
        Assert.True(BotGrenadeSafety.BurstsTooClose(PlasmaBall, ChestAt(0, 0), ChestAt(0, 5f)));     // a target 5 m away (or a rail 5 m out on the line to a farther one)
        Assert.False(BotGrenadeSafety.BurstsTooClose(PlasmaBall, ChestAt(0, 0), ChestAt(0, 9f)));
        Assert.False(BotGrenadeSafety.BurstsTooClose(null, ChestAt(0, 0), ChestAt(0, 1f)));          // hit-scan: no projectile
        Assert.False(BotGrenadeSafety.BurstsTooClose(PlasmaBall with { Splash = null }, ChestAt(0, 0), ChestAt(0, 1f)));   // a plain bolt
    }

    [Fact]
    public void A_target_flying_at_the_bot_meets_the_ball_nearer_than_it_is_shown()
    {
        // A Raptor shown 9.5 m out flying at the heavy at 20 m/s (measured 2026-11-11: the ball met it 6.5 m from the heavy's chest and killed the heavy); the basic ball flies 100 m/s,
        // remote players are drawn 0.1 s late.
        var eye = new Vector3(0, 0, 1.8f);
        var shown = ChestAt(0, 9.5f, 1f);
        var flying = new Vector3(0, -20f, 0);
        Assert.False(BotGrenadeSafety.BurstsTooClose(PlasmaBall, ChestAt(0, 0), shown));                       // where it is shown: 9.5 m, a shot
        var meet = BotGrenadeSafety.MeetingPoint(eye, shown, flying, PlasmaBall.Speed, shownLate: 0.1f);
        Assert.InRange(meet.Y, 5.4f, 5.7f);                                                                     // 9.5 - 20 x (0.1 + 0.08)
        Assert.True(BotGrenadeSafety.BurstsTooClose(PlasmaBall, ChestAt(0, 0), meet));                          // where they meet: held
        // Moving away (or standing) it meets the ball no nearer than it is shown.
        Assert.True(BotGrenadeSafety.MeetingPoint(eye, shown, -flying, PlasmaBall.Speed, 0.1f).Y > shown.Y);
        Assert.Equal(shown, BotGrenadeSafety.MeetingPoint(eye, shown, Vector3.Zero, PlasmaBall.Speed, 0.1f));
        Assert.Equal(shown, BotGrenadeSafety.MeetingPoint(eye, shown, flying, 0f));                              // no speed, no delay: where it is
    }

    // ---------------------------------------------------------------- remote detonation

    [Fact]
    public void Waiting_rounds_are_not_set_off_with_the_bot_inside_their_reach()
    {
        float safety = 4f + BotGrenadeSafety.Margin;
        Assert.False(BotGrenadeSafety.MayDetonate([At(0f, 3f), At(0f, 20f)], ChestAt(0, 0), safety));
        Assert.True(BotGrenadeSafety.MayDetonate([At(0f, 7f), At(0f, 20f)], ChestAt(0, 0), safety));
        Assert.True(BotGrenadeSafety.MayDetonate([], ChestAt(0, 0), safety));
    }

    // ---------------------------------------------------------------- tracking: age, expiry, unseen, down (no deadlock)

    [Fact]
    public void An_own_grenade_is_tracked_by_age_claim_and_place_and_dropped_after_the_cap()
    {
        const BotGrenadeSafety.OwnHazard None = BotGrenadeSafety.OwnHazard.None, Landing = BotGrenadeSafety.OwnHazard.Landing,
            Grenade = BotGrenadeSafety.OwnHazard.Grenade, Drop = BotGrenadeSafety.OwnHazard.Drop;
        // Unseen (not claimed yet): the planned landing stands for it until UnseenDropSeconds, then it is dropped; without a planned landing it has no place.
        Assert.Equal(Landing, BotGrenadeSafety.TrackOwn(0.3f, claimed: false, seen: false, down: false, hasLanding: true, cleared: false));
        Assert.Equal(None, BotGrenadeSafety.TrackOwn(0.3f, claimed: false, seen: false, down: false, hasLanding: false, cleared: false));
        Assert.Equal(Landing, BotGrenadeSafety.TrackOwn(BotGrenadeSafety.UnseenDropSeconds - 0.05f, false, false, false, true, false));
        Assert.Equal(Drop, BotGrenadeSafety.TrackOwn(BotGrenadeSafety.UnseenDropSeconds + 0.05f, false, false, false, true, false));
        Assert.Equal(2.1f, BotGrenadeSafety.UnseenDropSeconds, 3);
        // Claimed and in flight: its planned landing; once it has been outside the bot's reach, its own place too (a bounce back off a wall).
        Assert.Equal(Landing, BotGrenadeSafety.TrackOwn(0.2f, claimed: true, seen: true, down: false, hasLanding: true, cleared: false));
        Assert.Equal(Landing | Grenade, BotGrenadeSafety.TrackOwn(0.5f, true, true, false, true, cleared: true));
        Assert.Equal(Grenade, BotGrenadeSafety.TrackOwn(0.5f, true, true, false, hasLanding: false, cleared: true));
        // Down: only its own place.
        Assert.Equal(Grenade, BotGrenadeSafety.TrackOwn(1.2f, true, true, down: true, hasLanding: true, cleared: false));
        // Gone from the snapshot (it went off): dropped.
        Assert.Equal(Drop, BotGrenadeSafety.TrackOwn(1.4f, true, seen: false, down: true, hasLanding: true, cleared: true));
        // The cap: a grenade still there after MaxHazardSeconds (a lost detonation) is a hazard no longer.
        Assert.Equal(Grenade, BotGrenadeSafety.TrackOwn(BotGrenadeSafety.MaxHazardSeconds - 0.1f, true, true, true, true, true));
        Assert.Equal(Drop, BotGrenadeSafety.TrackOwn(BotGrenadeSafety.MaxHazardSeconds + 0.1f, true, true, true, true, true));
        Assert.Equal(6f, BotGrenadeSafety.MaxHazardSeconds);
    }

    [Fact]
    public void A_grenade_is_down_when_stuck_or_slow_over_a_full_sample()
    {
        Assert.True(BotGrenadeSafety.IsDown(stuckOrAttached: true, moved: 9f, sampleSeconds: 0.01));
        Assert.True(BotGrenadeSafety.IsDown(false, moved: 1.0f, sampleSeconds: 0.15));          // under 10 m/s
        Assert.False(BotGrenadeSafety.IsDown(false, moved: 7.0f, sampleSeconds: 0.15));         // the throw's 47.5 m/s
        Assert.False(BotGrenadeSafety.IsDown(false, moved: 0.2f, sampleSeconds: 0.05));         // too short a sample to say
    }

    [Fact]
    public void A_waiting_round_is_a_hazard_for_the_cap_from_when_it_is_first_seen()
    {
        Assert.True(BotGrenadeSafety.RoundIsHazard(firstSeen: 10.0, now: 10.0));
        Assert.True(BotGrenadeSafety.RoundIsHazard(10.0, 15.9));
        Assert.False(BotGrenadeSafety.RoundIsHazard(10.0, 16.1));
        // A capped round no longer holds the bot: the move into it goes on.
        var input = Input(0f, 1f, InputButtons.None);
        var round = new BotGrenadeSafety.Hazard(At(0f, 6f), 4f + BotGrenadeSafety.Margin);
        BotGrenadeSafety.Hazard[] live = BotGrenadeSafety.RoundIsHazard(10.0, 12.0) ? [round] : [];
        BotGrenadeSafety.Hold(input, At(0, 0), ChestAt(0, 0), live, out var kind);
        Assert.Equal(BotGrenadeSafety.HoldKind.Held, kind);
        BotGrenadeSafety.Hazard[] capped = BotGrenadeSafety.RoundIsHazard(10.0, 16.5) ? [round] : [];
        Assert.Equal(input, BotGrenadeSafety.Hold(input, At(0, 0), ChestAt(0, 0), capped, out kind));
        Assert.Equal(BotGrenadeSafety.HoldKind.None, kind);
    }

    [Fact]
    public void Weapons_that_can_leave_a_waiting_round_are_told_from_ones_whose_shots_burst_on_contact()
    {
        var gun = new WeaponSpec("test_gun", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.1f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
        Assert.False(BotGrenadeSafety.LeavesWaitingRounds(gun));                                                       // hit-scan
        Assert.False(BotGrenadeSafety.LeavesWaitingRounds(null));
        var cannon = gun with { Delivery = DeliveryKind.Projectile, Projectile = PlasmaBall };
        Assert.False(BotGrenadeSafety.LeavesWaitingRounds(cannon));                                                    // bursts on contact (the plasma cannon)
        Assert.True(BotGrenadeSafety.LeavesWaitingRounds(cannon with { Projectile = PlasmaBall with { Sticky = true } }));           // sticks and waits
        Assert.True(BotGrenadeSafety.LeavesWaitingRounds(cannon with { Projectile = PlasmaBall with { DetonateOnCollision = false } }));   // rests and waits
        Assert.True(BotGrenadeSafety.LeavesWaitingRounds(cannon with { Projectile = GrenadeTests.FragBall with { MaxActive = 6 } }));     // the launcher (remote)
        Assert.False(BotGrenadeSafety.LeavesWaitingRounds(cannon with { Projectile = PlasmaBall with { Sticky = true, Splash = null } })); // no blast: nothing to keep clear of
    }

    // ---------------------------------------------------------------- the back-out's give-up (a cornered bot is not stuck)

    [Fact]
    public void A_back_out_that_gets_no_farther_gives_up_after_the_limit_one_that_gains_never_does()
    {
        // Against a wall: the distance stays (a little sway is not progress).
        BotGrenadeSafety.BackOutProgress? p = null;
        bool giveUp = false;
        double t = 0;
        for (; t <= 3.0 && !giveUp; t += 1.0 / 30)
            p = BotGrenadeSafety.StepBackOut(p, 5f + 0.1f * MathF.Sin((float)t * 7f), t, out giveUp);
        Assert.True(giveUp);
        Assert.InRange(t, BotGrenadeSafety.GiveUpSeconds, BotGrenadeSafety.GiveUpSeconds + 0.1);
        // Backing out freely at 4 m/s: every tick gains, no give-up before it is out.
        p = null; giveUp = false;
        for (t = 0; t <= 3.0; t += 1.0 / 30)
        {
            p = BotGrenadeSafety.StepBackOut(p, 2f + 4f * (float)t, t, out giveUp);
            Assert.False(giveUp);
        }
        // Slow progress restarts the clock each time it gains ProgressMetres.
        p = BotGrenadeSafety.StepBackOut(null, 3f, 0.0, out _);
        p = BotGrenadeSafety.StepBackOut(p, 3.3f, 1.4, out giveUp);
        Assert.False(giveUp);
        Assert.Equal(1.4, p.Value.Since, 6);
        p = BotGrenadeSafety.StepBackOut(p, 3.4f, 2.8, out giveUp);   // only 0.1 m more: no progress, but 1.4 s since the last gain
        Assert.False(giveUp);
        BotGrenadeSafety.StepBackOut(p, 3.4f, 2.95, out giveUp);
        Assert.True(giveUp);
    }

    [Fact]
    public void A_bot_cornered_inside_its_own_blast_gives_up_and_moves_on_its_own_until_it_is_out()
    {
        // The bot backs into a wall 1 m behind it (its position is clamped there); the hazard is 3 m ahead.
        var hazards = new BotGrenadeSafety.Hazard[] { new(At(0f, 3f), BotGrenadeSafety.SafetyDistance(Frag)) };
        var feet = At(0, 0);
        BotGrenadeSafety.BackOutProgress? p = null;
        bool gaveUp = false;
        var wanted = Input(1f, 1f, InputButtons.Fire | InputButtons.Melee);   // what the fight wants: forward and to the side, swinging
        double t = 0;
        for (; t < 3.0 && !gaveUp; t += 1.0 / 30)
        {
            var held = BotGrenadeSafety.Hold(wanted, feet, feet + new Vector3(0, 0, Chest), hazards, out var kind);
            Assert.Equal(BotGrenadeSafety.HoldKind.BackedOut, kind);
            Assert.True(held.MoveY < 0f);
            feet = new Vector3(feet.X, MathF.Max(-1f, feet.Y + held.MoveY * 6f / 30f), 0f);   // the wall stops it at y = -1
            p = BotGrenadeSafety.StepBackOut(p, BotGrenadeSafety.DistanceInside(hazards, feet + new Vector3(0, 0, Chest))!.Value, t, out gaveUp);
        }
        Assert.True(gaveUp, "a cornered bot gives up");
        Assert.True(t < 2.6, $"gave up at {t:F2} s");
        // While it is still inside, the blast that holds it is left out (its own moves, melee included); once out, the holds apply again.
        Assert.True(BotGrenadeSafety.StillGivenUp(gaveUp, hazards, feet + new Vector3(0, 0, Chest)));
        Assert.False(BotGrenadeSafety.StillGivenUp(gaveUp, hazards, ChestAt(-14f, 0f)));
        Assert.False(BotGrenadeSafety.StillGivenUp(false, hazards, feet + new Vector3(0, 0, Chest)));
        Assert.Equal(3f + 1f, BotGrenadeSafety.DistanceInside(hazards, At(0f, -1f))!.Value, 3);
        Assert.Null(BotGrenadeSafety.DistanceInside(hazards, At(0f, -14f)));
    }

    // ---------------------------------------------------------------- a throw under way

    [Fact]
    public void A_throw_under_way_keeps_the_farther_of_the_press_landing_and_the_current_one()
    {
        var chest = ChestAt(0, 0);
        var pressed = At(0f, 14f);
        Assert.Equal(pressed, BotGrenadeSafety.FartherLanding(chest, pressed, At(0f, 9f)));    // the target ran in: the aim stays at the safe press point
        Assert.Equal(At(0f, 17f), BotGrenadeSafety.FartherLanding(chest, pressed, At(0f, 17f)));   // it ran off: the aim follows it
        Assert.Equal(At(3f, 14f), BotGrenadeSafety.FartherLanding(chest, pressed, At(3f, 14f)));
        // The farther one is never inside the safety distance when the press was checked.
        float safety = BotGrenadeSafety.SafetyDistance(Frag);
        Assert.True(Vector3.Distance(chest, BotGrenadeSafety.FartherLanding(chest, pressed, At(0f, 2f))) >= safety);
    }

    // ---------------------------------------------------------------- the pistol for a splash weapon at close range

    [Fact]
    public void A_splash_shot_held_for_a_second_takes_a_loaded_pistol_and_the_range_brings_it_back()
    {
        float safety = BotGrenadeSafety.SafetyDistance(PlasmaBall);   // 8.5 m
        BotGrenadeSafety.HeldRun? run = null;
        double t = 0;
        // A heavy at 5 m from its target: every tick's shot is held.
        for (; t < 0.95; t += 1.0 / 30)
        {
            Assert.True(BotGrenadeSafety.BurstsTooClose(PlasmaBall, ChestAt(0, 0), ChestAt(0, 5f)));
            run = BotGrenadeSafety.NoteHeld(run, t);
            Assert.False(BotGrenadeSafety.TakePistol(BotGrenadeSafety.HeldFor(run, t), pistolClip: 8));
        }
        for (; t < 1.05; t += 1.0 / 30) run = BotGrenadeSafety.NoteHeld(run, t);
        Assert.True(BotGrenadeSafety.TakePistol(BotGrenadeSafety.HeldFor(run, t), pistolClip: 8));
        Assert.False(BotGrenadeSafety.TakePistol(BotGrenadeSafety.HeldFor(run, t), pistolClip: 0));   // an empty holstered pistol does not reload: no use
        // A gap (a tick without a target, a fired shot) longer than HeldGapSeconds ends the run.
        Assert.Equal(0.0, BotGrenadeSafety.HeldFor(run, t + 0.3));
        var fresh = BotGrenadeSafety.NoteHeld(run, t + 0.3);
        Assert.Equal(0.0, BotGrenadeSafety.HeldFor(fresh, t + 0.3));
        // Back to the splash weapon only with its burst point the margin beyond the safety distance (no swapping at the edge).
        Assert.False(BotGrenadeSafety.BackToSplash(safety + 0.5f, safety));
        Assert.False(BotGrenadeSafety.BackToSplash(safety + 1.9f, safety));
        Assert.True(BotGrenadeSafety.BackToSplash(safety + BotGrenadeSafety.PistolBackMargin, safety));
        Assert.Equal(1f, BotGrenadeSafety.PistolAfterSeconds);
    }

    // ---------------------------------------------------------------- who plays by the rules

    [Fact]
    public void Classic_PvP_bots_follow_the_rules_Musou_Mode_and_the_restore_flag_do_not()
    {
        Assert.True(BotGrenadeSafety.Applies(ContentExtras.None, restore: false));     // faithful
        Assert.True(BotGrenadeSafety.Applies(ContentExtras.Orks, restore: false));     // extended Classic (the Ork Boy is a Classic player)
        Assert.False(BotGrenadeSafety.Applies(ContentExtras.None, restore: true));     // --old-bot-throws
        Assert.False(BotGrenadeSafety.Applies(ContentExtras.Musou, restore: false));   // Musou Mode's co-op bots keep their script
        Assert.False(BotGrenadeSafety.Applies(ContentExtras.Musou | ContentExtras.Orks, restore: false));
        Assert.Equal("old-bot-throws", BotGrenadeSafety.RestoreFlag);
        Assert.Contains(BotGrenadeSafety.RestoreFlag, LeanBot.LeanSafeAids);   // a lean bot keeps it (it steers, it draws nothing)
    }

    [Fact]
    public void The_musou_co_op_bot_script_is_not_touched()
    {
        // Musou Mode's co-op bot returns its own input before the Classic fight (ExtrasBotInput) and the hold skips that tick; its script names none of these rules.
        var file = Path.Combine(RepoRoot(), "client", "Extras", "Musou", "Tfw", "NetworkView.TfwCoopBot.cs");
        Assert.True(File.Exists(file), file);
        string text = File.ReadAllText(file);
        Assert.DoesNotContain("BotGrenadeSafety", text);
        Assert.DoesNotContain("BotGrenadeHold", text);
        Assert.DoesNotContain("GrenadeSafetyOn", text);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmReimpl.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    // ---------------------------------------------------------------- in the simulation: the old aim against the new

    /// <summary>The frag with Classic PvP's ground bounce (the owner's 2026-10-29 rule: the Havok material, restitution 0.65 and friction 2, so a grenade stays about where it lands).</summary>
    static readonly SimConfig OwnerBounce = GrenadeTests.Config with
    {
        Thrown = [Frag with { Projectile = GrenadeTests.FragBall with { Rigid = GrenadeTests.Rigid with { GroundMaterial = true, BodyFriction = 2f, BodyRestitution = 0.65f } } }, GrenadeTests.Krak, GrenadeTests.Bomb],
    };

    /// <summary>Throws from the origin (yaw 0, facing +Y) at a bystander <paramref name="distance"/> m ahead with <paramref name="pitch"/>; the thrower's own blast damage and the blast's place.</summary>
    static (float OwnDamage, Vector3 Blast) ThrowAt(float distance, float pitch)
    {
        var config = OwnerBounce;
        var world = new World(config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, distance, 0), MathF.PI)]);
        world.Spawn(1, 0, equipmentChoice: 0);   // the frag
        world.Spawn(2, 1);
        float own = 0f; Vector3? blast = null;
        PlayerInput Thrower(int id, InputButtons b) => new(0, 0, 0, id == 1 ? 0f : MathF.PI, id == 1 ? b : InputButtons.None, id == 1 ? pitch : 0f);
        world.Step(id => Thrower(id, InputButtons.Grenade));
        for (int k = 0; k < 400 && blast is null; k++)
        {
            world.Step(id => Thrower(id, InputButtons.None));
            foreach (var h in world.LastHits) if (h.Target == 1 && h.Shooter == 1) own += h.ShieldDamage + h.HealthDamage;
            if (world.LastDetonations.Count > 0) blast = world.LastDetonations[0].Position;
        }
        Assert.NotNull(blast);
        return (own, blast!.Value);
    }

    [Fact]
    public void The_old_aim_at_a_close_target_blows_the_thrower_up_the_new_aim_lands_beyond_the_safety_distance()
    {
        var config = OwnerBounce;
        float eye = config.EyeHeight, chest = HitVolumes.CentreHeight(config.For(0)), safety = BotGrenadeSafety.SafetyDistance(Frag);
        // The old rule: a target 8 m away is thrown at, aimed 2 m in front (8 m short): the thrower takes its own blast's full damage.
        float oldPitch = MathF.Atan2(-eye, BotGrenadeSafety.OldAimDistance(8f));
        var (oldOwn, oldBlast) = ThrowAt(8f, oldPitch);
        output.WriteLine($"old aim at 8 m: blast {oldBlast.Y:F1} m out, own damage {oldOwn:F0}");
        Assert.True(oldOwn >= 100f, $"own damage {oldOwn}");
        // The new rule refuses 8 m; at 14 m the aim goes to the landing point (the target's feet): the blast is beyond the safety distance and the thrower is not hit.
        Assert.Equal(BotGrenadeSafety.ThrowVerdict.TargetTooClose, BotGrenadeSafety.CheckThrow(new(0, 0, chest), new(0, 8f, chest), new(0, 8f, 0), safety, 20f));
        Assert.Equal(BotGrenadeSafety.ThrowVerdict.Throw, BotGrenadeSafety.CheckThrow(new(0, 0, chest), new(0, 14f, chest), new(0, 14f, 0), safety, 20f));
        float newPitch = MathF.Atan2(-eye, 14f);
        var (newOwn, newBlast) = ThrowAt(14f, newPitch);
        output.WriteLine($"new aim at 14 m: blast {newBlast.Y:F1} m out, own damage {newOwn:F0}");
        Assert.True(Vector3.Distance(newBlast, new Vector3(0, 0, chest)) >= safety, $"blast at {newBlast}");
        Assert.Equal(0f, newOwn);
        // The old aim at the same 14 m target (6 m in front) hit the thrower too.
        var (old14, _) = ThrowAt(14f, MathF.Atan2(-eye, BotGrenadeSafety.OldAimDistance(14f)));
        output.WriteLine($"old aim at 14 m: own damage {old14:F0}");
        Assert.True(old14 > 0f);
    }
}
