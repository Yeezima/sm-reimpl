using System.Numerics;
using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// fix/hitboxes-grenades-bots (protocol 37; the owner's playtest of 2026-10-29), synthetic: the flight hit volumes (chosen as the client chooses its
/// flight clip, posed by the motion's vertical angle, used by shots), the view fraction (the client's floor and fraction, the wire, the server's rewind
/// between two ticks), the test bots' damage scale (World, the Hello's flag, the server option) and the owner's shorter grenade bounce. No game data.
/// </summary>
public class FlyingHitboxTests
{
    // ---------------------------------------------------------------- flight hit volumes

    static readonly HitVolume[] Rest =
    [
        HitVolume.Sphere("Head", HitZone.Head, new Vector3(0, 0, 1.9f), 0.3f),
        HitVolume.Capsule("Chest", HitZone.Body, new Vector3(0, 0, 0.6f), new Vector3(0, 0, 1.4f), 0.35f),
    ];

    /// <summary>The rest volumes moved by <paramref name="by"/> (character frame): a stand-in for a posed set.</summary>
    static HitVolume[] Moved(Vector3 by) => Rest.Select(v => v with { A = v.A + by, B = v.B + by }).ToArray();

    static readonly Vector3 UpShift = new(0, 1.5f, 0), ForwardShift = new(0, 0, -0.5f), LowShift = new(1f, 0, 0);

    /// <summary>Three poses in the client's lookup order: high U/F (sets at fractions 0, 0.5, 1 differ by Z), high F/F, low F/F.</summary>
    static readonly FlightVolumes Flight = new([
        new FlightPose("SM_JMP_Zero_U_Aim_F", 'U', 'F', false, [Moved(UpShift), Moved(UpShift + new Vector3(0, 0, -0.3f)), Moved(UpShift + new Vector3(0, 0, -0.6f))]),
        new FlightPose("SM_JMP_Zero_F_Aim_F", 'F', 'F', false, [Moved(ForwardShift), Moved(ForwardShift)]),
        new FlightPose("SM_JMP_ZeroLo_F_Aim_F", 'F', 'F', true, [Moved(LowShift), Moved(LowShift)]),
    ]);

    static readonly SimConfig FlightConfig = JumpPackTests.Config with { HitVolumes = Rest, FlightHitVolumes = Flight };

    static PlayerState Flier(Vector3 velocity, float yaw = 0f, float pitch = 0f) =>
        new(1, new Vector3(0, 0, 5f), velocity, yaw, false) { Alive = true, Jump = JumpPhase.Ascent, Pitch = pitch, Class = 0 };

    [Fact]
    public void The_pose_is_chosen_as_the_client_chooses_its_flight_clip()
    {
        Assert.True(Flight.IsValid);
        // Below the neutral speed the direction is U; the U/F high clip; a missing aim falls back to the level aim.
        Assert.Equal(0, Flight.Choose(Flier(new Vector3(0, 1f, 20f)), false));
        Assert.Equal(0, Flight.Choose(Flier(new Vector3(0, 1f, 20f), pitch: 1f), false));
        // Forward (facing +Y at yaw 0): F; far from the ground the high set, near it the low one.
        Assert.Equal(1, Flight.Choose(Flier(new Vector3(0, 8f, 2f)), false));
        Assert.Equal(2, Flight.Choose(Flier(new Vector3(0, 8f, 2f)), true));
        // Turned: a yaw of 90° left faces −X, so moving −X is forward.
        Assert.Equal(1, Flight.Choose(Flier(new Vector3(-8f, 0, 0), yaw: MathF.PI / 2f), false));
        // Backward has no pose here: (B, F) then (U, F), as the client's fallback.
        Assert.Equal(0, Flight.Choose(Flier(new Vector3(0, -8f, 0)), false));
        // Low and moving sideways: the low set has no L or U pose; its first pose.
        Assert.Equal(2, Flight.Choose(Flier(new Vector3(-8f, 0, 0)), true));
        // The fraction is the client's: straight up 0, level 0.5, straight down 1.
        Assert.Equal((0f, 0.5f, 1f), (FlightPoseRules.Target(10f, 0f), FlightPoseRules.Target(0f, 10f), FlightPoseRules.Target(-10f, 0f)));
        Assert.Equal(0.25f, FlightPoseRules.Target(10f, 10f), 4);
        Assert.Equal(0.5f, FlightPoseRules.Target(float.NaN, 1f));
    }

    [Fact]
    public void Only_a_flying_player_is_hit_with_the_flight_volumes()
    {
        var c = FlightConfig;
        var rising = Flier(new Vector3(0, 0, 20f));
        Assert.Same(Flight.Poses[0].Sets[0], c.HitBody(rising, null).HitVolumes);
        Assert.Same(Flight.Poses[0].Sets[2], c.HitBody(rising with { Velocity = new Vector3(0, 0, -20f), Jump = JumpPhase.Fall }, null).HitVolumes);
        Assert.Same(Flight.Poses[0].Sets[1], c.HitBody(rising with { Velocity = new Vector3(0, 1f, 0f), Jump = JumpPhase.Hang }, null).HitVolumes);
        Assert.Same(c.HitBody(rising, null), c.HitBody(rising, null));   // cached
        // Not flying: grounded, not jumping, in the take-off, pounding, or dead: the rest volumes.
        Assert.Same(Rest, c.HitBody(rising with { Grounded = true }, null).HitVolumes);
        Assert.Same(Rest, c.HitBody(rising with { Jump = JumpPhase.None }, null).HitVolumes);
        Assert.Same(Rest, c.HitBody(rising with { Jump = JumpPhase.TakeOff }, null).HitVolumes);
        Assert.Same(Rest, c.HitBody(rising with { Jump = JumpPhase.Dive }, null).HitVolumes);
        Assert.Same(Rest, c.HitBody(rising with { Alive = false }, null).HitVolumes);
        // Without flight volumes (tests, placeholders) nothing changes.
        Assert.Same(Rest, (c with { FlightHitVolumes = null }).HitBody(rising, null).HitVolumes);
        // A class body with volumes of its own (the Ork's) keeps them in any state.
        var own = Moved(new Vector3(0, 0, 0.1f));
        var withBody = c with
        {
            Bodies = [BodySpec.Of(c, "own") with { HitVolumes = own }],
            Classes = [.. c.Classes, c.Classes[1] with { Name = "own body", Body = 0 }],
        };
        Assert.Same(own, withBody.HitBody(rising with { Class = 2 }, null).HitVolumes);
    }

    [Fact]
    public void The_config_hash_covers_the_flight_clips_but_not_their_posed_values()
    {
        var plain = JumpPackTests.Config with { HitVolumes = Rest };
        Assert.Equal(plain.Hash(), (plain with { FlightHitVolumes = null }).Hash());
        Assert.NotEqual(plain.Hash(), FlightConfig.Hash());
        // S1 of the review: posed floats come from platform maths (a Linux server, a Windows client), so they are not hashed.
        var nudged = new FlightVolumes([Flight.Poses[0] with { Sets = [Moved(UpShift), Moved(UpShift), Moved(UpShift + new Vector3(0, 0, 0.01f))] }, Flight.Poses[1], Flight.Poses[2]]);
        Assert.Equal(FlightConfig.Hash(), (FlightConfig with { FlightHitVolumes = nudged }).Hash());
        // The clips, their keys, order and step counts and the take-off clip are.
        Assert.NotEqual(FlightConfig.Hash(), (FlightConfig with { FlightHitVolumes = new FlightVolumes([Flight.Poses[1], Flight.Poses[0], Flight.Poses[2]]) }).Hash());
        Assert.NotEqual(FlightConfig.Hash(), (FlightConfig with { FlightHitVolumes = new FlightVolumes([Flight.Poses[0] with { Clip = "x" }, Flight.Poses[1], Flight.Poses[2]]) }).Hash());
        Assert.NotEqual(FlightConfig.Hash(), (FlightConfig with { FlightHitVolumes = new FlightVolumes([Flight.Poses[0] with { Sets = Flight.Poses[0].Sets.Take(2).ToArray() }, Flight.Poses[1], Flight.Poses[2]]) }).Hash());
        Assert.NotEqual(FlightConfig.Hash(), (FlightConfig with { FlightHitVolumes = Flight with { TakeOff = [Rest, Rest], TakeOffClip = FlightPoseRules.TakeOffClip } }).Hash());
        // Computed once per instance (S3): the same answer, and a copy hashes itself.
        Assert.Equal(FlightConfig.Hash(), FlightConfig.Hash());
        Assert.Equal(FlightConfig.Hash(), (FlightConfig with { }).Hash());
    }

    /// <summary>The assault (1) at the origin jumps straight up (Jump held); the tactical (2) 20 m away along +X shoots once, at a flying tick, at
    /// <paramref name="aimLocal"/> above the assault's feet (world offset at yaw 0); whether the shot hit it.</summary>
    static bool ShootFlier(SimConfig c, Func<PlayerState, Vector3> aimLocal)
    {
        var world = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(20f, 0, 0), 0f)]);
        world.Spawn(1, 0); world.Spawn(2, 1);
        for (int k = 0; k < 60; k++)
        {
            var target = world.Players[1];
            bool fire = SimConfig.Flying(target) && target.Velocity.Z > 5f && (c.FlightHitVolumes is null ? target.JumpTicks >= 0 && world.Tick > 12 : world.FlightViewOf(1).Phase == FlightViewPhase.Flight);
            PlayerInput Shooter()
            {
                if (!fire) return new PlayerInput(0, 0, 0, MathF.PI / 2f, InputButtons.None);
                var eye = Combat.EyePosition(world.Players[2], c);
                var to = target.Position + aimLocal(target) - eye;
                return new PlayerInput(0, 0, 0, MathF.Atan2(-to.X, to.Y), InputButtons.Fire, MathF.Asin(to.Z / to.Length()), ViewTick: world.Tick);
            }
            var shot = Shooter();
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, InputButtons.Jump) : shot);
            if (fire) return world.LastHits.Any(h => h.Target == 1 && h.Shooter == 2);
        }
        throw new Xunit.Sdk.XunitException("the assault never flew");
    }

    [Fact]
    public void A_shot_at_the_drawn_flight_pose_hits_and_one_at_the_rest_pose_misses()
    {
        // Rising straight up: pose U/F at fraction 0 (moved 1.5 m along +Y).
        var posedChest = (PlayerState _) => UpShift + new Vector3(0, 0, 1.0f);
        var restChest = (PlayerState _) => new Vector3(0, 0, 1.0f);
        Assert.True(ShootFlier(FlightConfig, posedChest));
        Assert.False(ShootFlier(FlightConfig, restChest));
        // Before (no flight volumes): the other way round.
        var before = FlightConfig with { FlightHitVolumes = null };
        Assert.False(ShootFlier(before, posedChest));
        Assert.True(ShootFlier(before, restChest));
    }

    // ---------------------------------------------------------------- the view fraction

    [Fact]
    public void The_client_sends_the_tick_at_or_before_its_render_tick_and_the_fraction_in_256ths()
    {
        Assert.Equal((10u, 0f), GameClient.ViewOf(10.0));
        Assert.Equal((10u, 0.5f), GameClient.ViewOf(10.5));
        Assert.Equal((10u, 64 / 256f), GameClient.ViewOf(10.2501));
        Assert.Equal((10u, 255 / 256f), GameClient.ViewOf(10.99999));
        Assert.Equal((0u, 0f), GameClient.ViewOf(-3.0));
        Assert.Equal((0u, 0f), GameClient.ViewOf(double.NaN));
        // On the wire: a byte, as quantized; anything outside [0, 1) is 0.
        foreach (float f in new[] { 0f, 0.5f, 0.99f, 1f, -0.2f, float.NaN, 0.123f })
        {
            var input = new PlayerInput(5, 0, 0, 0, InputButtons.None, ViewTick: 4, ViewFraction: f);
            var back = Assert.IsType<InputBatch>(Protocol.Read(Protocol.Write(new InputBatch([input], 1, 1)))).Inputs[0];
            Assert.Equal(Protocol.Quantize(input).ViewFraction, back.ViewFraction);
            Assert.True(back.ViewFraction is >= 0f and < 1f);
        }
        Assert.Equal(0.5f, Protocol.Quantize(new PlayerInput(0, 0, 0, 0, InputButtons.None, ViewFraction: 0.5f)).ViewFraction);
        Assert.Equal(0f, new PlayerInput(0, 0, 0, 0, InputButtons.None, ViewFraction: 1.5f).Sanitized().ViewFraction);
    }

    [Fact]
    public void Between_interpolates_like_the_client_and_keeps_a_respawn_where_it_was()
    {
        var a = new PlayerState(1, new Vector3(0, 0, 1), new Vector3(0, 0, 10), 3.0f, false) { Alive = true, Health = 50f };
        var b = a with { Position = new Vector3(0, 0, 1.4f), Velocity = new Vector3(0, 0, 8), Yaw = -3.0f, Health = 10f };
        var mid = World.Between(a, b, 0.5f);
        Assert.Equal(new Vector3(0, 0, 1.2f), mid.Position);
        Assert.Equal(new Vector3(0, 0, 9), mid.Velocity);
        Assert.Equal(MathF.PI, MathF.Abs(mid.Yaw), 2);   // the shorter way round (3.0 → −3.0 crosses π)
        Assert.Equal(50f, mid.Health);   // every other field the earlier state's
        Assert.Equal(a, World.Between(a, b, 0f));
        Assert.Equal(a, World.Between(a, b with { Position = new Vector3(10, 0, 0) }, 0.5f));   // a respawn: the earlier state
        Assert.Equal(a, World.Between(a, b with { Alive = false }, 0.5f));
    }

    /// <summary>
    /// Tiny volumes (5 cm spheres) on a fast riser: the shooter fires once at the body sphere as it was between two ticks by <paramref name="aimFraction"/>,
    /// with <c>ViewTick</c> two ticks back (or, with <paramref name="currentTick"/>, the tick that ends as the shot resolves: <c>ViewTick</c> = Tick − 1 in
    /// the step, whose next tick is the players now) and <paramref name="fraction"/>; with <paramref name="cone"/> a narrow cone (0.4°) instead of a ray.
    /// Whether the shot hit. A twin world one step ahead gives the next tick's state for <paramref name="currentTick"/>.
    /// </summary>
    static bool ShootBetween(float fraction, float aimFraction, bool cone = false, bool currentTick = false)
    {
        var tiny = new[] { HitVolume.Sphere("Head", HitZone.Head, new Vector3(0, 0, 1.9f), 0.05f), HitVolume.Sphere("Chest", HitZone.Body, new Vector3(0, 0, 1.0f), 0.05f) };
        var c = JumpPackTests.Config with { HitVolumes = tiny };
        if (cone)
        {
            var spec = new ConeSpec(30f, 0.4f, 0f, 0f, new DamageSpec(40f, 0f, 1f, 1f, 1f, 1f, false), [new(0f, 1f), new(30f, 1f)], [new(0f, 1f), new(1f, 1f)]);
            var gun = new WeaponSpec("test_cone", WeaponSlot.Ranged, TriggerKind.SemiAuto, 40f, 0.3f, 5, 10, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f)
            { Delivery = DeliveryKind.Cone, Cone = spec };
            c = c with { Weapons = [c.Weapons[0], gun] };
        }
        World Make()
        {
            var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(20f, 0, 0), 0f)]);
            w.Spawn(1, 0); w.Spawn(2, 1);
            return w;
        }
        var world = Make();
        var twin = Make();
        PlayerState? before = null;
        PlayerInput Idle() => new(0, 0, 0, MathF.PI / 2f, InputButtons.None);
        for (int k = 0; k < 60; k++)
        {
            var now = world.Players[1];
            bool fire = before is { } a && SimConfig.Flying(a) && a.Velocity.Z > 10f;
            PlayerInput shot = Idle();
            if (fire)
            {
                var (from, to) = currentTick ? (now, Next()) : (before!.Value, now);
                var point = Vector3.Lerp(from.Position, to.Position, aimFraction) + new Vector3(0, 0, 1.0f);
                var line = point - Combat.EyePosition(world.Players[2], c);
                shot = new PlayerInput(0, 0, 0, MathF.Atan2(-line.X, line.Y), InputButtons.Fire, MathF.Asin(line.Z / line.Length()),
                    ViewTick: currentTick ? world.Tick : world.Tick - 1, ViewFraction: fraction);
                Assert.True(Vector3.Distance(from.Position, to.Position) > 0.25f);   // well beyond the spheres' size
            }
            else twin.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, InputButtons.Jump) : Idle());
            before = now;
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, InputButtons.Jump) : shot);
            if (fire) return world.LastHits.Any(h => h.Target == 1 && h.Shooter == 2);
        }
        throw new Xunit.Sdk.XunitException("the assault never rose fast");

        PlayerState Next()
        {
            twin.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, InputButtons.Jump) : Idle());
            return twin.Players[1];
        }
    }

    [Fact]
    public void A_shot_rewinds_to_the_view_fraction_between_two_ticks()
    {
        Assert.True(ShootBetween(0.5f, 0.5f));    // after: drawn halfway, rewound halfway
        Assert.False(ShootBetween(0f, 0.5f));     // before: the whole tick only
        Assert.True(ShootBetween(0f, 0f));
        Assert.True(ShootBetween(0.75f, 0.75f));
        Assert.False(ShootBetween(0.75f, 0f));
    }

    [Fact]
    public void A_shot_at_the_tick_that_ends_interpolates_toward_the_players_now()
    {
        Assert.True(ShootBetween(0.5f, 0.5f, currentTick: true));
        Assert.False(ShootBetween(0f, 0.5f, currentTick: true));
        Assert.True(ShootBetween(0f, 0f, currentTick: true));
    }

    [Fact]
    public void A_cone_rewinds_to_the_view_fraction_too()
    {
        Assert.True(ShootBetween(0.5f, 0.5f, cone: true));
        Assert.False(ShootBetween(0f, 0.5f, cone: true));
        Assert.True(ShootBetween(0f, 0f, cone: true));
    }

    [Fact]
    public void Whole_tick_paths_keep_the_nearest_tick_and_shots_use_the_fraction()
    {
        var tiny = new[] { HitVolume.Sphere("Head", HitZone.Head, new Vector3(0, 0, 1.9f), 0.05f), HitVolume.Sphere("Chest", HitZone.Body, new Vector3(0, 0, 1.0f), 0.05f) };
        var world = new World(JumpPackTests.Config with { HitVolumes = tiny }, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(20f, 0, 0), 0f)]);
        world.Spawn(1, 0); world.Spawn(2, 1);
        var at = new Dictionary<uint, Vector3>();
        for (int k = 0; k < 20; k++)
        {
            at[world.Tick] = world.Players[1].Position;
            world.Step(id => new PlayerInput(0, 0, 0, 0f, id == 1 ? InputButtons.Jump : InputButtons.None));
        }
        at[world.Tick] = world.Players[1].Position;
        uint t = world.Tick - 3;
        Assert.True(Vector3.Distance(at[t], at[t + 1]) > 0.25f);
        PlayerInput In(float f) => new(0, 0, 0, 0f, InputButtons.None, ViewTick: t, ViewFraction: f);
        // Melee, the lock and the charge: the tick at or before below a half, the next from a half on (the nearest, as before the fraction).
        Assert.Equal(at[t], world.WholeTickView(In(0f))[1].Position);
        Assert.Equal(at[t], world.WholeTickView(In(0.49f))[1].Position);
        Assert.Equal(at[t + 1], world.WholeTickView(In(0.5f))[1].Position);
        Assert.Equal(at[t + 1], world.WholeTickView(In(0.9f))[1].Position);
        // Shots: between the two.
        Assert.Equal(Vector3.Lerp(at[t], at[t + 1], 0.25f), world.ShotView(In(0.25f))[1].Position);
        Assert.Equal(at[t], world.ShotView(In(0f))[1].Position);
    }

    [Fact]
    public void A_bounded_view_tick_drops_the_fraction()
    {
        var input = new PlayerInput(50, 0, 0, 0, InputButtons.None, ViewTick: 40, ViewFraction: 0.5f);
        Assert.Equal((40u, 0.5f), (GameServer.WithBoundedView(input, 40).ViewTick, GameServer.WithBoundedView(input, 40).ViewFraction));
        Assert.Equal((44u, 0f), (GameServer.WithBoundedView(input, 44).ViewTick, GameServer.WithBoundedView(input, 44).ViewFraction));
        Assert.Equal((51u, 0f), (GameServer.WithBoundedView(input with { ViewTick = 0 }, 51).ViewTick, GameServer.WithBoundedView(input with { ViewTick = 0 }, 51).ViewFraction));
    }

    // ---------------------------------------------------------------- the pose rules shared by the client and the server (S4 of the review)

    [Fact]
    public void The_clip_keys_are_looked_up_in_the_clients_order()
    {
        var s = Flier(new Vector3(0, 8f, 0), pitch: 0.5f);
        Assert.Equal((('F', 'U'), ('F', 'F'), ('U', 'U'), ('U', 'F')), FlightPoseRules.Candidates(s));
        Assert.Equal((('L', 'D'), ('L', 'F'), ('U', 'D'), ('U', 'F')), FlightPoseRules.Candidates(Flier(new Vector3(-8f, 1f, 0), pitch: -0.5f)));
        Assert.Equal((('U', 'F'), ('U', 'F'), ('U', 'F'), ('U', 'F')), FlightPoseRules.Candidates(Flier(new Vector3(1f, 1f, 20f))));
        Assert.Equal((('B', 'F'), ('B', 'F'), ('U', 'F'), ('U', 'F')), FlightPoseRules.Candidates(Flier(new Vector3(0, -8f, 0))));
        // The client's constants are these (NetworkView.Jump uses FlightPoseRules).
        Assert.Equal((0.35f, 3f, 1f / 3f, "SM_JMP_Curve_F_Lo_Far_01"), (FlightPoseRules.AimThreshold, FlightPoseRules.NeutralSpeed, FlightPoseRules.TakeOffBranch, FlightPoseRules.TakeOffClip));
        Assert.Equal((13.5f, 1f, 0.15f), (SmContent.JumpFlightPose.LowHighSwitch, SmContent.JumpFlightPose.LowHighHysteresis, SmContent.JumpFlightPose.DampSeconds));
    }

    [Fact]
    public void The_view_follows_the_clients_take_off_window_damping_and_hysteresis()
    {
        const float dt = 1f / 30f;
        var track = new FlightTrack();
        var ground = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Alive = true, Jump = JumpPhase.TakeOff };
        var v = FlightPoseRules.Step(track, ground, dt, 0f, 0.2f);
        Assert.Equal((FlightViewPhase.TakeOff, 0f), (v.Phase, v.Seconds));
        for (int k = 1; k < 6; k++) v = FlightPoseRules.Step(track, ground, dt, 0f, 0.2f);
        Assert.Equal(5 * dt, v.Seconds, 4);
        // In the air but inside the take-off window (1/3 s after the press): still the take-off clip.
        var up = ground with { Grounded = false, Jump = JumpPhase.Ascent, Velocity = new Vector3(0, 0, 20f) };
        for (int k = 6; k < 10; k++) { v = FlightPoseRules.Step(track, up, dt, 3f, 0.2f); Assert.Equal(FlightViewPhase.TakeOff, v.Phase); }
        // From the branch on: the flight clip, starting at the target, then damped toward a new one.
        v = FlightPoseRules.Step(track, up, dt, 3f, 0.2f);
        Assert.Equal((FlightViewPhase.Flight, 0f, true), (v.Phase, v.Fraction, v.Low));
        var level = up with { Velocity = new Vector3(0, 10f, 0) };
        v = FlightPoseRules.Step(track, level, dt, 3f, 0.2f);
        Assert.Equal(0.5f * (1f - MathF.Exp(-dt / FlightPoseRules.DampSeconds)), v.Fraction, 4);
        // The low set holds inside the hysteresis band and changes beyond it.
        Assert.True(FlightPoseRules.Step(track, level, dt, 14f, 0.2f).Low);
        Assert.False(FlightPoseRules.Step(track, level, dt, 14.6f, 0.2f).Low);
        Assert.False(FlightPoseRules.Step(track, level, dt, 13f, 0.2f).Low);
        // Landing ends the view; seen in the air first (no take-off seen), the press is the take-off delay back.
        Assert.Equal(FlightViewPhase.None, FlightPoseRules.Step(track, ground with { Jump = JumpPhase.None }, dt, 0f, 0.2f).Phase);
        var seen = new FlightTrack();
        v = FlightPoseRules.Step(seen, up, dt, 3f, 0.2f);
        Assert.Equal((FlightViewPhase.TakeOff, 0.2f), (v.Phase, v.Seconds));
        for (int k = 0; k < 4; k++) v = FlightPoseRules.Step(seen, up, dt, 3f, 0.2f);
        Assert.Equal(FlightViewPhase.Flight, v.Phase);
    }

    [Fact]
    public void The_server_keeps_each_players_view_and_hits_the_take_off_and_flight_poses()
    {
        var takeOff = Enumerable.Range(0, 11).Select(k => (IReadOnlyList<HitVolume>)Moved(new Vector3(0, 0, -0.05f * k))).ToArray();
        var c = FlightConfig with { FlightHitVolumes = Flight with { TakeOff = takeOff, TakeOffClip = FlightPoseRules.TakeOffClip } };
        var world = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(20f, 0, 0), 0f)]);
        world.Spawn(1, 0); world.Spawn(2, 1);
        var phases = new List<FlightViewPhase>();
        for (int k = 0; k < 25; k++)
        {
            world.Step(id => new PlayerInput(0, 0, 0, 0f, id == 1 ? InputButtons.Jump : InputButtons.None));
            var view = world.FlightViewOf(1);
            phases.Add(view.Phase);
            var body = c.HitBody(world.Players[1], view).HitVolumes;
            if (view.Phase == FlightViewPhase.TakeOff) Assert.Same(takeOff[Math.Min(10, (int)MathF.Round(view.Seconds * 30f))], body);
            if (view.Phase == FlightViewPhase.Flight) Assert.Contains(body, Flight.Poses[0].Sets);
        }
        // The press, 10 ticks of the take-off clip (to 1/3 s), then the flight; the bystander shows none.
        Assert.Equal(Enumerable.Repeat(FlightViewPhase.TakeOff, 10), phases.Take(10));
        Assert.Equal(FlightViewPhase.Flight, phases[10]);
        Assert.Equal(FlightViewPhase.None, world.FlightViewOf(2).Phase);
        // Without flight volumes the server keeps no view.
        var plain = new World(JumpPackTests.Config, null, [new SpawnPoint(Vector3.Zero, 0f)]);
        plain.Spawn(1, 0);
        for (int k = 0; k < 12; k++) plain.Step(_ => new PlayerInput(0, 0, 0, 0f, InputButtons.Jump));
        Assert.Equal(FlightViewPhase.None, plain.FlightViewOf(1).Phase);
    }

    /// <summary>Every input of every client as the server receives it.</summary>
    sealed class AllInputs(IServerTransport inner) : IServerTransport
    {
        public readonly List<PlayerInput> Inputs = new();
        public void Send(int c, byte[] packet) => inner.Send(c, packet);
        public void Disconnect(int c) => inner.Disconnect(c);
        public void Pin(int c) => inner.Pin(c);
        public bool TryReceive(out int c, out byte[] packet)
        {
            if (!inner.TryReceive(out c, out packet)) return false;
            if (Protocol.Read(packet) is InputBatch b) Inputs.AddRange(b.Inputs);
            return true;
        }
    }

    [Fact]
    public void A_melee_lock_tick_snaps_to_a_held_snapshot_with_no_fraction_and_other_ticks_carry_one()
    {
        // 40 ms with jitter puts the render clock between whole ticks; one client taps melee every 20 ticks, the other stands near it.
        AllInputs? spy = null;
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(7), 0), 11, 2, wrapServer: t => spy = new AllInputs(t));
        h.Run(TimeSpan.FromSeconds(6), (index, tick) => new PlayerInput(tick, 0, 0, 0f, index == 0 && tick % 20 == 0 ? InputButtons.Melee : InputButtons.None));
        var inputs = spy!.Inputs.DistinctBy(i => (i.Tick, i.MoveX, i.Buttons, i.ViewTick, i.ViewFraction)).ToList();
        var presses = inputs.Where(i => (i.Buttons & InputButtons.Melee) != 0 && i.ViewTick > 0).ToList();
        Assert.True(presses.Count >= 5);
        Assert.All(presses, i => Assert.Equal(0f, i.ViewFraction));   // the lock's tick: a held snapshot exactly
        Assert.Contains(inputs, i => i.ViewFraction > 0f);           // every other tick: the fraction toward the next
        Assert.All(inputs, i => Assert.True(i.ViewFraction is >= 0f and < 1f));
    }

    // ---------------------------------------------------------------- the test bots' damage scale

    static float DamageTo(World world, int target) => world.LastHits.Where(x => x.Target == target).Sum(x => x.ShieldDamage + x.HealthDamage);

    static float Shot(float? scale)
    {
        var c = GrenadeTests.Config;
        var world = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 10f, 0), MathF.PI)]);
        world.Spawn(1, 0); world.Spawn(2, 1);
        if (scale is { } s) world.SetDamageScale(1, s);
        world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, InputButtons.Fire) : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
        return DamageTo(world, 2);
    }

    static float Swing(float? scale)
    {
        var c = GrenadeTests.Config;
        var world = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 1.2f, 0), MathF.PI)]);
        world.Spawn(1, 0); world.Spawn(2, 1);
        if (scale is { } s) world.SetDamageScale(1, s);
        float total = 0f;
        for (int k = 0; k < 40; k++)
        {
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, k == 0 ? InputButtons.Melee : InputButtons.None) : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
            total += world.LastHits.Where(x => x.Target == 2 && x.Melee).Sum(x => x.ShieldDamage + x.HealthDamage);
        }
        return total;
    }

    static float Blast(float? scale)
    {
        var c = GrenadeTests.Config;
        var world = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 4f, 0), MathF.PI)]);
        world.Spawn(1, 0, equipmentChoice: 0); world.Spawn(2, 1);
        if (scale is { } s) world.SetDamageScale(1, s);
        float total = 0f;
        for (int k = 0; k < 120; k++)
        {
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, k == 0 ? InputButtons.Grenade : InputButtons.None, -0.9f) : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
            total += world.LastHits.Where(x => x.Target == 2 && x.Splash).Sum(x => x.ShieldDamage + x.HealthDamage);
        }
        return total;
    }

    [Fact]
    public void A_scaled_player_deals_that_fraction_of_every_kind_of_damage()
    {
        foreach (var deal in new Func<float?, float>[] { Shot, Swing, Blast })
        {
            float full = deal(null), scaled = deal(0.34f);
            Assert.True(full > 0f);
            Assert.Equal(full * 0.34f, scaled, 3);
            Assert.Equal(full, deal(1f), 3);
            Assert.Equal(0f, deal(0f), 3);
        }
    }

    [Fact]
    public void The_scale_is_kept_per_id_until_it_leaves_or_joins_again()
    {
        var world = new World(GrenadeTests.Config, null, [new SpawnPoint(Vector3.Zero, 0f)]);
        world.Spawn(1, 0);
        Assert.Equal(1f, world.DamageScaleOf(1));
        world.SetDamageScale(1, 0.34f);
        Assert.Equal(0.34f, world.DamageScaleOf(1));
        Assert.Equal(1f, world.DamageScaleOf(2));
        world.SetDamageScale(1, 2f);   // only ever lowers (S2)
        Assert.Equal(1f, world.DamageScaleOf(1));
        world.SetDamageScale(1, float.NaN);
        Assert.Equal(1f, world.DamageScaleOf(1));
        world.SetDamageScale(1, 0.5f);
        world.Spawn(1, 0);   // a new join of the id
        Assert.Equal(1f, world.DamageScaleOf(1));
        world.SetDamageScale(1, 0.5f);
        world.Remove(1);
        Assert.Equal(1f, world.DamageScaleOf(1));
    }

    [Fact]
    public void A_bot_says_so_in_its_hello_and_the_server_scales_only_bots()
    {
        var hello = new Hello(Protocol.Version, 1, 2, 3, Flags: Hello.BotFlag);
        var back = Assert.IsType<Hello>(Protocol.Read(Protocol.Write(hello)));
        Assert.True(back.Bot);
        Assert.False(Assert.IsType<Hello>(Protocol.Read(Protocol.Write(hello with { Flags = 0 })))!.Bot);
        Assert.Equal(Protocol.HelloBytes, Protocol.Write(hello).Length);

        var h = new Harness(LinkConditions.Perfect, 5, 0, serverOptions: new ServerOptions { BotDamageScale = 0.34f });
        var bot = h.AddClient(nonce: 100, options: new ClientOptions { Bot = true });
        var human = h.AddClient(nonce: 101);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.True(bot.PlayerId > 0 && human.PlayerId > 0);
        Assert.Equal(0.34f, h.Server.World.DamageScaleOf(bot.PlayerId));
        Assert.Equal(1f, h.Server.World.DamageScaleOf(human.PlayerId));

        // Off by default: a bot deals full damage.
        var off = new Harness(LinkConditions.Perfect, 5, 0);
        var plainBot = off.AddClient(nonce: 100, options: new ClientOptions { Bot = true });
        off.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(1f, off.Server.World.DamageScaleOf(plainBot.PlayerId));
    }

    [Fact]
    public void The_server_option_parses_a_factor_from_0_to_1()
    {
        Assert.True(ServerCommandLine.TryParse(["--bot-damage-scale", "0.34"], out var a, out _));
        Assert.Equal(0.34f, a.BotDamageScale);
        Assert.True(ServerCommandLine.TryParse([], out var d, out _));
        Assert.Equal(1f, d.BotDamageScale);
        Assert.False(ServerCommandLine.TryParse(["--bot-damage-scale", "-1"], out _, out _));
        Assert.False(ServerCommandLine.TryParse(["--bot-damage-scale", "1.01"], out _, out _));   // a bot's flag may only lower its damage (S2)
        Assert.True(ServerCommandLine.TryParse(["--bot-damage-scale", "1"], out var one, out _));
        Assert.Equal(1f, one.BotDamageScale);
        Assert.False(ServerCommandLine.TryParse(["--bot-damage-scale", "x"], out _, out _));
        Assert.False(ServerCommandLine.TryParse(["--bot-damage-scale"], out _, out _));
        Assert.Contains("--bot-damage-scale", ServerCommandLine.Usage);
    }

    // ---------------------------------------------------------------- the owner's shorter grenade bounce

    static readonly RigidSpec Material = GrenadeTests.Rigid with { BodyFriction = 2f, BodyRestitution = 0.65f };

    [Fact]
    public void A_ground_bounce_with_the_body_material_loses_speed_to_restitution_and_friction()
    {
        var on = Material with { GroundMaterial = true };
        // Normal part × 0.65; the friction impulse 2 × 1.65 × 5 = 16.5 m/s off the speed along the ground, never reversing it.
        var steep = on.Bounce(new Vector3(10f, 0, -5f), Vector3.UnitZ);
        Assert.Equal(0f, steep.X, 4); Assert.Equal(3.25f, steep.Z, 4);
        var shallow = on.Bounce(new Vector3(30f, 0, -5f), Vector3.UnitZ);
        Assert.Equal(13.5f, shallow.X, 3); Assert.Equal(3.25f, shallow.Z, 4);
        // Off, or without a usable material: the plain reflection (the data's rule).
        Assert.Equal(new Vector3(10f, 0, 5f), Material.Bounce(new Vector3(10f, 0, -5f), Vector3.UnitZ));
        Assert.Equal(new Vector3(10f, 0, 5f), (GrenadeTests.Rigid with { GroundMaterial = true }).Bounce(new Vector3(10f, 0, -5f), Vector3.UnitZ));
        // Leaving the surface already: unchanged reflection.
        Assert.Equal(new Vector3(10f, 0, -5f), on.Bounce(new Vector3(10f, 0, 5f), Vector3.UnitZ));
    }

    /// <summary>A frag thrown at <paramref name="pitch"/> on flat ground: horizontal distances of its first ground contact and of its detonation, and the detonation tick.</summary>
    static (float Land, float End, uint Tick) Throw(RigidSpec rigid, float pitch)
    {
        var frag = GrenadeTests.Frag with { Projectile = GrenadeTests.FragBall with { Rigid = rigid } };
        var c = GrenadeTests.Config with { Thrown = [frag, GrenadeTests.Krak, GrenadeTests.Bomb] };
        var world = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f)]);
        world.Spawn(1, 0, equipmentChoice: 0);
        float land = float.NaN;
        for (int k = 0; k < 200; k++)
        {
            world.Step(_ => new PlayerInput(0, 0, 0, 0f, k == 0 ? InputButtons.Grenade : InputButtons.None, pitch));
            if (world.LastDetonations.Count > 0)
            {
                var d = world.LastDetonations[0].Position;
                return (land, new Vector2(d.X, d.Y).Length(), world.Tick);
            }
            if (world.Projectiles.Count > 0 && float.IsNaN(land) && world.Projectiles[0].Landed)
                land = new Vector2(world.Projectiles[0].Position.X, world.Projectiles[0].Position.Y).Length();
        }
        throw new Xunit.Sdk.XunitException("no detonation");
    }

    [Fact]
    public void The_owners_bounce_is_shorter_and_the_fuse_keeps_its_time()
    {
        foreach (float pitch in new[] { -0.9f, -0.5f, -0.25f, 0f })
        {
            var data = Throw(Material, pitch);
            var owner = Throw(Material with { GroundMaterial = true }, pitch);
            Assert.Equal(data.Land, owner.Land, 3);   // the flight is the same
            Assert.Equal(data.Tick, owner.Tick);       // and so is the fuse
            Assert.True(owner.End - owner.Land < 1.5f, $"pitch {pitch}: {owner.End - owner.Land:0.00} m after landing");
            Assert.True(data.End - data.Land > 2f * (owner.End - owner.Land) + 1f, $"pitch {pitch}: data {data.End - data.Land:0.00} m, owner {owner.End - owner.Land:0.00} m");
        }
    }

    [Fact]
    public void The_config_hash_covers_the_bounce_rule_and_the_data_rule_hashes_as_before()
    {
        var frag = GrenadeTests.Frag with { Projectile = GrenadeTests.FragBall with { Rigid = Material } };
        var data = GrenadeTests.Config with { Thrown = [frag] };
        var plain = GrenadeTests.Config with { Thrown = [GrenadeTests.Frag] };
        Assert.Equal(plain.Hash(), data.Hash());   // the material alone (read, unused) does not change the hash
        var owner = GrenadeTests.Config with { Thrown = [frag with { Projectile = frag.Projectile with { Rigid = Material with { GroundMaterial = true } } }] };
        Assert.NotEqual(data.Hash(), owner.Hash());
    }
}
