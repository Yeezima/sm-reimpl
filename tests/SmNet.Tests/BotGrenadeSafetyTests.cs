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

    // ---------------------------------------------------------------- remote detonation

    [Fact]
    public void Waiting_rounds_are_not_set_off_with_the_bot_inside_their_reach()
    {
        float safety = 4f + BotGrenadeSafety.Margin;
        Assert.False(BotGrenadeSafety.MayDetonate([At(0f, 3f), At(0f, 20f)], ChestAt(0, 0), safety));
        Assert.True(BotGrenadeSafety.MayDetonate([At(0f, 7f), At(0f, 20f)], ChestAt(0, 0), safety));
        Assert.True(BotGrenadeSafety.MayDetonate([], ChestAt(0, 0), safety));
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
