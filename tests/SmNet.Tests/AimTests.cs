using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M20 (protocol 12): the shot direction (convergence on the crosshair point) and its server-side cone, aim mode, the
/// shooter's hit totals in snapshots, and an end-to-end audit of hit registration with the shot aimed where the client
/// draws the target. Synthetic configs only (no game data); the aiming speeds typed in are the originals recorded in
/// spec/behavior.md.
/// </summary>
public class AimTests(ITestOutputHelper output)
{
    static readonly float Deg = MathF.PI / 180f;

    static float AngleBetween(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f));

    // ---------------------------------------------------------------- shot direction

    [Fact]
    public void Zero_offsets_shoot_exactly_along_the_view()
    {
        var c = new SimConfig();
        foreach (var (yaw, pitch) in new[] { (0f, 0f), (1.234f, -0.4f), (-2.9f, 1.3f), (7.1f, 0.2f) })
        {
            var input = new PlayerInput(1, 0, 0, yaw, InputButtons.Fire, pitch).Sanitized();
            Assert.Equal((input.Yaw, input.Pitch), Combat.ShotAim(input, c));
            Assert.Equal(Combat.AimDirection(input.Yaw, input.Pitch), Combat.ShotDirection(input, c));
        }
    }

    [Theory]
    [InlineData(0.3f, 0.1f, 5f, 0f)]
    [InlineData(-1.0f, -0.3f, -10f, 6f)]
    [InlineData(2.5f, 0.6f, 3f, -12f)]
    public void A_shot_direction_inside_the_cone_is_kept(float yaw, float pitch, float dyDeg, float dpDeg)
    {
        var c = new SimConfig();
        var wanted = Combat.AimDirection(yaw + dyDeg * Deg, pitch + dpDeg * Deg);
        Assert.True(AngleBetween(wanted, Combat.AimDirection(yaw, pitch)) < c.ShotConeDegrees * Deg);
        var input = new PlayerInput(1, 0, 0, yaw, InputButtons.Fire, pitch).WithShotDirection(wanted * 7.5f);
        Assert.True(AngleBetween(wanted, Combat.ShotDirection(input, c)) < 1e-4f);
        // After a trip through an input batch (1/10000 rad steps) it is still within 1e-4 rad.
        var wire = Assert.IsType<InputBatch>(Protocol.Read(Protocol.Write(new InputBatch([input])))).Inputs[0];
        Assert.Equal(Protocol.Quantize(input), wire);
        Assert.True(AngleBetween(wanted, Combat.ShotDirection(wire, c)) < 2e-4f);
    }

    [Theory]
    [InlineData(40f, 0f)]
    [InlineData(-90f, 10f)]
    [InlineData(0f, -60f)]
    [InlineData(25f, 25f)]
    public void A_shot_direction_outside_the_cone_is_turned_back_to_its_edge(float dyDeg, float dpDeg)
    {
        var c = new SimConfig();
        float yaw = 0.4f, pitch = 0.1f;
        var view = Combat.AimDirection(yaw, pitch);
        var wanted = Combat.AimDirection(yaw + dyDeg * Deg, Math.Clamp(pitch + dpDeg * Deg, -MathF.PI / 2, MathF.PI / 2));
        var shot = Combat.ShotDirection(new PlayerInput(1, 0, 0, yaw, InputButtons.Fire, pitch).WithShotDirection(wanted), c);
        Assert.Equal(c.ShotConeDegrees * Deg, AngleBetween(view, shot), 1e-3f);
        // Toward the requested side: in the plane of view and request, between them.
        var normal = Vector3.Normalize(Vector3.Cross(view, wanted));
        Assert.True(MathF.Abs(Vector3.Dot(normal, shot)) < 1e-3f);
        Assert.True(AngleBetween(shot, wanted) < AngleBetween(view, wanted));
        // Straight behind has no side to turn to: the view.
        var behind = new PlayerInput(1, 0, 0, yaw, InputButtons.Fire, 0f) with { ShotYawOffset = MathF.PI };
        Assert.True(AngleBetween(Combat.AimDirection(yaw, 0f), Combat.ShotDirection(behind, c)) < 1e-3f);
    }

    [Fact]
    public void Non_finite_or_huge_offsets_are_sanitized_and_the_wire_clamps_them()
    {
        var input = new PlayerInput(1, 0, 0, 0f, InputButtons.Fire, 0f, 0, float.NaN, float.PositiveInfinity).Sanitized();
        Assert.Equal((0f, 0f), (input.ShotYawOffset, input.ShotPitchOffset));
        var big = new PlayerInput(1, 0, 0, 0f, InputButtons.Fire, 0f, 0, 50f, -50f).Sanitized();
        Assert.Equal((PlayerInput.MaxShotOffset, -PlayerInput.MaxShotOffset), (big.ShotYawOffset, big.ShotPitchOffset));
        var wire = Assert.IsType<InputBatch>(Protocol.Read(Protocol.Write(new InputBatch([new PlayerInput(1, 0, 0, 0, InputButtons.None, 0, 0, 9f, -9f)])))).Inputs[0];
        Assert.Equal((3.2767f, -3.2767f), (wire.ShotYawOffset, wire.ShotPitchOffset));
        Assert.Equal(new PlayerInput(1, 0, 0, 0f, InputButtons.None), new PlayerInput(1, 0, 0, 0f, InputButtons.None).WithShotDirection(Vector3.Zero));
    }

    [Fact]
    public void Input_batches_carry_the_aim_button_and_drop_unknown_buttons()
    {
        var input = new PlayerInput(7, 0.5f, -1f, 1f, InputButtons.Aim | InputButtons.Fire | InputButtons.Grenade, 0.2f, 5, 0.01f, -0.02f);
        var bytes = Protocol.Write(new InputBatch([input], 3, 4));
        Assert.Equal(1 + 4 + 4 + 1 + 40, bytes.Length);   // 36 bytes per input since protocol 12, 38 since protocol 32 (the melee target), 39 since 37 (the view fraction), 40 since 43 (the body lag)
        Assert.Equal(40, Protocol.InputBytes);
        Assert.Equal(Protocol.Quantize(input), Assert.IsType<InputBatch>(Protocol.Read(bytes)).Inputs[0]);
        bytes[1 + 4 + 4 + 1 + 4 + 12 + 1] = 0xFF;   // the high byte of the buttons: bits 8..11 (Aim, SlotPistol, SlotPrimary; protocol 14; MeleeHeavy, protocol 17; Interact, protocol 26) kept, bit 13 added content's ExtraAction (extras wire 2) kept, bits 14..15 unknown and dropped
        Assert.Equal(InputButtons.Aim | InputButtons.SlotPistol | InputButtons.SlotPrimary | InputButtons.MeleeHeavy | InputButtons.Interact | InputButtons.ExtraAction | InputButtons.Fire | InputButtons.Grenade,
            Assert.IsType<InputBatch>(Protocol.Read(bytes)).Inputs[0].Buttons);
    }

    /// <summary>Shooter 1 at the origin facing +Y; targets facing it. Default config: the legacy bolter, 20 damage, full auto.</summary>
    static World WorldWith(SimConfig config, params Vector3[] targets)
    {
        var spawns = new List<SpawnPoint> { new(Vector3.Zero, 0f) };
        spawns.AddRange(targets.Select(p => new SpawnPoint(p, MathF.PI)));
        var world = new World(config, null, spawns);
        for (int i = 0; i < spawns.Count; i++) world.Spawn(i + 1);
        return world;
    }

    static Vector3 Chest(Vector3 feet, SimConfig c) => feet + new Vector3(0, 0, (c.BodyBottom + c.BodyTop) / 2f);

    [Fact]
    public void Hit_scan_shots_go_along_the_shot_direction_and_the_server_cone_clamps_it()
    {
        var c = new SimConfig { HealthMax = 1e6f };
        Assert.Equal(30f, c.ShotConeDegrees);
        var near = new Vector3(10f, 20f, 0f);   // 26.6° to the right of the view: inside the 30° cone
        var far = new Vector3(-20f, 20f, 0f);   // 45° to the left: outside it
        var world = WorldWith(c, near, far);
        PlayerInput Aimed(Vector3 target) =>
            new PlayerInput(0, 0, 0, 0f, InputButtons.Fire, 0f).WithShotDirection(Chest(target, c) - Combat.EyePosition(world.Players[1], c));
        PlayerInput Idle(int id) => new(0, 0, 0, id == 1 ? 0f : MathF.PI, InputButtons.None);

        world.Step(id => id == 1 ? Aimed(near) : Idle(id));
        var hit = Assert.Single(world.LastHits);
        Assert.Equal((1, 2), (hit.Shooter, hit.Target));
        for (int k = 0; k < 10; k++) world.Step(Idle);   // the cooldown passes
        world.Step(id => id == 1 ? Aimed(far) : Idle(id));
        Assert.Empty(world.LastHits);
        Assert.Contains(world.LastShots, s => s.Shooter == 1);   // it fired, along the cone's edge, and missed
        // Control: the same shot with the view turned toward the far target hits it.
        for (int k = 0; k < 10; k++) world.Step(Idle);
        float yawFar = MathF.Atan2(-far.X, far.Y);
        world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, yawFar, InputButtons.Fire, 0f).WithShotDirection(Chest(far, c) - Combat.EyePosition(world.Players[1], c)) : Idle(id));
        Assert.Equal(3, Assert.Single(world.LastHits).Target);
    }

    static readonly ProjectileSpec Dart = new("ebps\\test\\dart", 30f, 0f, "raycast", 0f, [new(0f, 0f)], true, true, false, 0, 0f, 0f, 0, null,
        new SplashSpec(1f, new DamageSpec(1f, 0f, 1f, 1f, 0f, 0f, false), [new(0f, 1f)], true, false, "target_all"));

    [Fact]
    public void Projectiles_and_thrown_items_leave_along_the_shot_direction()
    {
        var launcher = new WeaponSpec("test_launcher", WeaponSlot.Ranged, TriggerKind.SemiAuto, 0f, 0.2f, 10, 20, 0.6f, 150f, 150f, 150f, 1f, 1f, 1f, 0f, 0f)
        { Delivery = DeliveryKind.Projectile, Projectile = Dart };
        var c = new SimConfig { Weapons = [launcher], Classes = [new ClassLoadout("test", 0, -1, [])] };
        var world = WorldWith(c);
        var wanted = Combat.AimDirection(0.15f, 0.1f);   // 8.6° right and 5.7° up of a level view along +Y
        var input = new PlayerInput(0, 0, 0, 0f, InputButtons.Fire, 0f).WithShotDirection(wanted);
        world.Step(_ => input);
        var dart = Assert.Single(world.Projectiles);
        Assert.True(AngleBetween(wanted, dart.Velocity) < 1e-4f, $"launched {AngleBetween(wanted, dart.Velocity) / Deg:F3}° off");

        // A thrown frag: released after the toss; its direction is the release tick's shot direction raised by ExtraAimAngle.
        var g = GrenadeTests.Config;
        var thrower = new World(g, null, [new SpawnPoint(Vector3.Zero, 0f)]);
        thrower.Spawn(1, 0);
        var release = new PlayerInput(0, 0, 0, 0f, InputButtons.Grenade, 0f).WithShotDirection(Combat.AimDirection(-0.2f, 0.05f));
        for (int k = 0; k < 30 && thrower.Projectiles.Count == 0; k++) thrower.Step(_ => release);
        var frag = Assert.Single(thrower.Projectiles);
        var expected = Combat.AimDirection(-0.2f, 0.05f + GrenadeTests.Frag.Projectile.ExtraAimAngle * Deg);
        Assert.True(AngleBetween(expected, frag.Velocity) < 1e-3f, $"thrown {AngleBetween(expected, frag.Velocity) / Deg:F3}° off");
    }

    [Fact]
    public void The_melta_cone_follows_the_shot_direction()
    {
        var c = new SimConfig
        {
            Weapons = [MeltaTests.Melta], Classes = [new ClassLoadout("test - melta", 0, -1, []), new ClassLoadout("test - target", -1, -1, [])],
            HealthMax = 10000f, ShieldMax = 0f, HealthRegenDelay = 1e6f,
        };
        var target = new Vector3(0f, 18f, 0f);   // 18 m ahead; the view 25° to its left puts it 7.6 m off the cone axis (radius there 3.3 m)
        int Hits(bool converge)
        {
            var spawns = new List<SpawnPoint> { new(Vector3.Zero, 0f), new(target, MathF.PI) };
            var world = new World(c, null, spawns);
            world.Spawn(1, 0); world.Spawn(2, 1);
            float yaw = 25f * Deg;   // the view 25° left of the target (inside the 30° the server allows the shot to turn)
            var input = new PlayerInput(0, 0, 0, yaw, InputButtons.Fire, 0f);
            if (converge) input = input.WithShotDirection(Chest(target, c) - Combat.EyePosition(world.Players[1], c));
            world.Step(id => id == 1 ? input : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
            return world.LastHits.Count(h => h.Target == 2 && h.Cone);
        }
        Assert.Equal(1, Hits(converge: true));
        Assert.Equal(0, Hits(converge: false));
    }

    // ---------------------------------------------------------------- shot origin (firing along the camera ray)

    /// <summary>The original exploration camera at level (CameraRig navigationSet_standing at 90°): right, forward, up of the feet.</summary>
    static readonly Vector3 RigCamera = new(0.644f, -3.384f, 2.382f);

    /// <summary>The point of the ray from <paramref name="from"/> along <paramref name="direction"/> nearest <paramref name="point"/>.</summary>
    internal static Vector3 NearestOnRay(Vector3 from, Vector3 direction, Vector3 point)
    {
        var d = Vector3.Normalize(direction);
        return from + d * Vector3.Dot(point - from, d);
    }

    [Theory]
    [InlineData(1.6f)]
    [InlineData(1.3f)]
    public void A_close_enemy_under_the_crosshair_is_hit_along_the_camera_ray(float lateral)
    {
        // The enemy stands 1.5 m in front of the shooter's eye plane, to the right; the crosshair (the rig camera's ray) is
        // on its chest. Seen from the eye that point is more than 30° off the view, so an eye-based shot would be clamped.
        var c = new SimConfig { HealthMax = 1e6f };
        var feet = new Vector3(lateral, 1.5f, 0f);
        var world = WorldWith(c, feet);
        var shooter = world.Players[1];
        var eye = Combat.EyePosition(shooter, c);
        var chest = Chest(feet, c);
        var camera = shooter.Position + RigCamera;   // yaw 0: right = +X, forward = +Y
        var ray = chest - camera;
        float yaw = MathF.Atan2(-ray.X, ray.Y), pitch = MathF.Atan2(ray.Z, new Vector2(ray.X, ray.Y).Length());
        Assert.True(AngleBetween(chest - eye, ray) > c.ShotConeDegrees * Deg, $"only {AngleBetween(chest - eye, ray) / Deg:F1}° from the eye");

        var origin = NearestOnRay(camera, ray, eye);
        var input = new PlayerInput(0, 0, 0, yaw, InputButtons.Fire, pitch).WithShot(origin, ray, eye);
        Assert.True(input.ShotOrigin.Length() < c.MaxShotOriginOffset, $"origin offset {input.ShotOrigin.Length():F2} m");
        world.Step(id => id == 1 ? input : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
        var hit = Assert.Single(world.LastHits);
        Assert.Equal((1, 2, HitZone.Body), (hit.Shooter, hit.Target, hit.Zone));
        Assert.Equal(Vector3.Distance(origin, chest) - c.BodyRadius, hit.Distance, 0.15f);   // it entered along the camera ray
        // Through the wire (centimetre origin, 1/10000 rad direction) the input still points at the chest.
        var wire = Assert.IsType<InputBatch>(Protocol.Read(Protocol.Write(new InputBatch([input])))).Inputs[0];
        Assert.Equal(Protocol.Quantize(input), wire);
        Assert.True(Vector3.Distance(Combat.ShotOrigin(shooter, wire, c), origin) < 0.01f);
    }

    [Fact]
    public void An_origin_offset_beyond_the_limit_is_shortened_and_zero_or_nonsense_means_the_eye()
    {
        var c = new SimConfig();
        var s = Combat.Respawned(new PlayerState(1, new Vector3(2, 3, 0), Vector3.Zero, 0f, true), c);
        var eye = Combat.EyePosition(s, c);
        var input = new PlayerInput(1, 0, 0, 0f, InputButtons.Fire, 0f, 0, 0f, 0f, 3f, 0f, 0f);
        Assert.Equal(1.5f, c.MaxShotOriginOffset);
        Assert.Equal(eye + new Vector3(1.5f, 0, 0), Combat.ShotOrigin(s, input, c));
        var within = input with { ShotOriginX = 0.6f, ShotOriginY = -0.8f, ShotOriginZ = 0.7f };
        Assert.Equal(eye + new Vector3(0.6f, -0.8f, 0.7f), Combat.ShotOrigin(s, within, c));
        Assert.Equal(eye, Combat.ShotOrigin(s, new PlayerInput(1, 0, 0, 0f, InputButtons.Fire), c));
        Assert.Equal(eye, Combat.ShotOrigin(s, input with { ShotOriginX = float.NaN }, c));
        Assert.Equal(eye, Combat.ShotOrigin(s, input, c with { MaxShotOriginOffset = 0f }));
        // WithShot from a non-finite origin keeps the eye; WithShotDirection leaves the origin as it was.
        var plain = new PlayerInput(1, 0, 0, 0f, InputButtons.Fire).WithShot(new Vector3(float.NaN, 0, 0), Vector3.UnitY, eye);
        Assert.Equal(Vector3.Zero, plain.ShotOrigin);
        Assert.Equal(within.ShotOrigin, within.WithShotDirection(Vector3.UnitY).ShotOrigin);
        Assert.NotEqual(new SimConfig().Hash(), (c with { MaxShotOriginOffset = 1.4f }).Hash());
    }

    [Fact]
    public void An_origin_behind_level_geometry_falls_back_to_the_eye()
    {
        // A wall at x = 0.5 (weapon collision), the shooter at the origin, a target at (5, 1.6) beyond the wall; the shot
        // would start 1 m to the right (past the wall) and go straight at the target's chest.
        var v = new[] { new Vector3(0.5f, -20, -1), new Vector3(0.5f, 20, -1), new Vector3(0.5f, 20, 10), new Vector3(0.5f, -20, 10) };
        var wall = new StaticMesh(v, [0, 1, 2, 0, 2, 3]);
        var c = new SimConfig { HealthMax = 1e6f };
        var level = new CollisionWorld(wall, wall);
        var target = new Vector3(5f, 1.6f, 0f);
        var world = new World(c, level, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(target, MathF.PI)]);
        world.Spawn(1); world.Spawn(2);
        var s = world.Players[1];
        var eye = Combat.EyePosition(s, c);
        var origin = eye + new Vector3(1f, 0, 0);
        Assert.Equal(eye, Combat.ShotOrigin(s, new PlayerInput(0, 0, 0, 0f, InputButtons.Fire).WithShot(origin, Vector3.UnitY, eye), c, level));
        Assert.Equal(origin, Combat.ShotOrigin(s, new PlayerInput(0, 0, 0, 0f, InputButtons.Fire).WithShot(origin, Vector3.UnitY, eye), c));   // without the level: accepted
        var input = new PlayerInput(0, 0, 0, MathF.Atan2(-(target.X - 1f), target.Y), InputButtons.Fire, 0f).WithShot(origin, Chest(target, c) - origin, eye);
        world.Step(id => id == 1 ? input : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
        Assert.Empty(world.LastHits);   // from the eye, the wall is in the way
        Assert.Contains(world.LastShots, x => x.Shooter == 1);
    }

    // ---------------------------------------------------------------- aim mode

    /// <summary>Original speeds (spec/behavior.md): standing run 6.5, strafe 6, back 4.5, sprint 10; aiming run 3, strafe 3, back 2.</summary>
    static SimConfig OriginalSpeeds(SimConfig? c = null) => (c ?? new SimConfig()) with
    {
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, AimRunSpeed = 3f, AimStrafeSpeed = 3f, AimBackSpeed = 2f,
        GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    static (float Speed, PlayerState State) TopSpeed(SimConfig c, float x, float y, InputButtons buttons, PlayerState? start = null)
    {
        var s = start ?? Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);
        for (int t = 1; t <= 60; t++) s = Combat.StepSelf(s, new PlayerInput((uint)t, x, y, 0f, buttons), c, out _);
        return (new Vector2(s.Velocity.X, s.Velocity.Y).Length(), s);
    }

    [Fact]
    public void Aim_mode_moves_at_the_aiming_speeds_without_sprint()
    {
        var c = OriginalSpeeds();
        Assert.Equal(6.5f, TopSpeed(c, 0, 1, InputButtons.None).Speed, 1e-3f);
        Assert.Equal(10f, TopSpeed(c, 0, 1, InputButtons.Sprint).Speed, 1e-3f);
        var (run, state) = TopSpeed(c, 0, 1, InputButtons.Aim);
        Assert.Equal(3f, run, 1e-3f);
        Assert.True(state.Aiming);
        Assert.Equal(3f, TopSpeed(c, 0, 1, InputButtons.Aim | InputButtons.Sprint).Speed, 1e-3f);   // aim overrides sprint
        Assert.Equal(3f, TopSpeed(c, 1, 0, InputButtons.Aim).Speed, 1e-3f);
        Assert.Equal(2f, TopSpeed(c, 0, -1, InputButtons.Aim).Speed, 1e-3f);
        Assert.False(TopSpeed(c, 0, 1, InputButtons.Sprint).State.Aiming);
    }

    [Fact]
    public void Aim_mode_needs_a_ranged_weapon_and_ends_in_melee_and_death()
    {
        // No weapon at all: no aim mode, normal speeds.
        var unarmed = OriginalSpeeds(new SimConfig { Weapons = [], Classes = [new ClassLoadout("test - unarmed", -1, -1, SimConfig.PlaceholderMeleeSteps)] });
        var (speed, state) = TopSpeed(unarmed, 0, 1, InputButtons.Aim);
        Assert.Equal(6.5f, speed, 1e-3f);
        Assert.False(state.Aiming);
        // A melee attack started while aiming leaves aim mode for its duration.
        var c = OriginalSpeeds();
        var s = TopSpeed(c, 0, 0, InputButtons.Aim).State;
        Assert.True(s.Aiming);
        s = Combat.StepSelf(s, new PlayerInput(61, 0, 0, 0f, InputButtons.Aim | InputButtons.Melee), c, out _);
        Assert.True(s.MeleeStep > 0);
        Assert.False(s.Aiming);
        // Death clears it; a dead player's tick keeps it clear.
        var dead = Combat.Killed(TopSpeed(c, 0, 0, InputButtons.Aim).State, c);
        Assert.False(dead.Aiming);
        Assert.False(Combat.StepSelf(dead, new PlayerInput(1, 0, 0, 0f, InputButtons.Aim), c, out _).Aiming);
    }

    [Fact]
    public void Snapshots_carry_the_aiming_flag_in_every_record_and_prediction_matches_the_server()
    {
        var p = new PlayerState(4, Vector3.One, Vector3.Zero, 0f, true) { Alive = true, Aiming = true };
        Assert.True(Protocol.Slim(p).Aiming);
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [p, p with { PlayerId = 5 }], Self: 5))));
        Assert.True(back.Players[0].Aiming);
        Assert.True(back.Players[1].Aiming);
        Assert.False(Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [p with { Aiming = false }])))).Players[0].Aiming);

        // End to end: a client walking in and out of aim mode is predicted exactly (no corrections) and others see the flag.
        var config = OriginalSpeeds();
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(5), 0.02), 5, 2, config);
        bool sawAiming = false;
        h.Run(TimeSpan.FromSeconds(6), (i, t) => i == 0
            ? new PlayerInput(t, MathF.Sin(t * 0.05f), 1f, t * 0.01f, (t / 40) % 2 == 0 ? InputButtons.Aim : InputButtons.Sprint)
            : new PlayerInput(t, 0, 0, 0, InputButtons.None),
            () => sawAiming |= h.Clients[1].RemotePlayers().Players.Any(q => q.PlayerId == h.Clients[0].PlayerId && q.Aiming));
        Assert.True(sawAiming);
        var errors = h.Clients[0].CorrectionErrors;
        output.WriteLine($"{errors.Count} snapshots compared, max correction {errors.DefaultIfEmpty().Max():F5} m");
        Assert.True(errors.Count > 50);
        Assert.All(errors, e => Assert.True(e < 1e-4f, $"correction {e} m"));
    }

    // ---------------------------------------------------------------- hit confirmation

    [Fact]
    public void The_hit_block_round_trips_and_refuses_unknown_flags()
    {
        var hits = new HitView(70000, 65535 + 12, 123, 300, 65535, HitFlags.Head | HitFlags.Killed, 0.5f, 2f);
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [], Hits: hits))));
        Assert.Equal(Protocol.Quantize(hits), back.Hits);
        Assert.Equal(new HitView(70000 - 65536, 11, 123, 300 - 256, 65535, HitFlags.Head | HitFlags.Killed, 128 / 255f, 1f), back.Hits);
        Assert.Equal(default(HitView), Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [])))).Hits);   // null travels as zeros
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, [], Hits: hits));
        bytes[^4] = 32;   // the flags byte: unknown bit (the shot count follows since protocol 16; 16 is Armoured since protocol 22)
        Assert.Null(Protocol.Read(bytes));
    }

    sealed class HitLog
    {
        public readonly List<HitEvent> Hits = new();
        uint _last = uint.MaxValue;
        public void Observe(World w) { if (w.Tick != _last) { _last = w.Tick; Hits.AddRange(w.LastHits); } }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    public void The_shooter_learns_every_hit_with_its_damage_victim_and_kills_even_through_loss(double loss)
    {
        // Small health and no regeneration, so the bolter kills and the target respawns several times.
        var config = new SimConfig { HealthMax = 60f, ShieldMax = 40f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f, RespawnTime = 0.5f };
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(5), loss), 7, 2, config,
            new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(1) });
        var log = new HitLog();
        h.Run(TimeSpan.FromSeconds(1.5), null, () => log.Observe(h.Server.World));
        var shooter = h.Clients[0]; int sid = shooter.PlayerId, tid = h.Clients[1].PlayerId;
        Assert.Null(shooter.TakeHits());
        var reports = new List<HitReport>();
        PlayerInput Input(int i, uint t)
        {
            if (i == 1 || t % 90 >= 60) return new PlayerInput(t, 0, 0, 0, InputButtons.None);
            var target = shooter.RemotePlayers().Players.FirstOrDefault(p => p.PlayerId == tid);
            if (target.PlayerId != tid) return new PlayerInput(t, 0, 0, 0, InputButtons.None);
            var eye = Combat.EyePosition(shooter.Predicted, config);
            var to = Chest(target.Position, config) - eye;
            return new PlayerInput(t, 0, 0, MathF.Atan2(-to.X, to.Y), InputButtons.Fire, 0f).WithShotDirection(to);
        }
        h.Run(TimeSpan.FromSeconds(8), Input, () => { log.Observe(h.Server.World); if (shooter.TakeHits() is { } r) reports.Add(r); });
        h.Run(TimeSpan.FromSeconds(1), null, () => { log.Observe(h.Server.World); if (shooter.TakeHits() is { } r) reports.Add(r); });

        var mine = log.Hits.Where(x => x.Shooter == sid && x.Target != sid).ToList();
        output.WriteLine($"loss {loss:P0}: server {mine.Count} hits, {mine.Count(x => x.Killed)} kills; client {reports.Count} reports, {reports.Sum(r => r.Hits)} hits, {reports.Sum(r => r.Kills)} kills");
        Assert.True(mine.Count >= 20);
        Assert.True(mine.Count(x => x.Killed) >= 2);
        Assert.Equal(mine.Count, reports.Sum(r => r.Hits));
        Assert.Equal(mine.Count(x => x.Killed), reports.Sum(r => r.Kills));
        Assert.Equal(mine.Sum(x => x.ShieldDamage), reports.Sum(r => r.ShieldDamage), 0.1f);
        Assert.Equal(mine.Sum(x => x.HealthDamage), reports.Sum(r => r.HealthDamage), 0.1f);
        Assert.All(reports, r => Assert.Equal(tid, r.LastVictim));
        Assert.Contains(reports, r => r.LastKilled && r.VictimHealth == 0f);
        Assert.Contains(reports, r => !r.LastKilled && r.VictimHealth > 0f && r.VictimHealth <= config.HealthMax);
        Assert.Null(shooter.TakeHits());
        // The target hit nobody.
        Assert.Null(h.Clients[1].TakeHits());
    }

    // ---------------------------------------------------------------- hit registration audit

    /// <summary>
    /// The client flow end to end: the shooter's camera sits over its right shoulder (the original exploration rig at level:
    /// 3.38 m back, 0.64 m right, 2.38 m up; CameraRig), the crosshair is the screen centre, so the view (yaw, pitch) is the
    /// camera's ray to the aim point on the target as the client draws it (<see cref="GameClient.RemotePlayers"/> at the
    /// moment the input is sampled), and the shot goes along that ray from its point nearest the eye (<see cref="PlayerInput.WithShot"/>).
    /// The target strafes across the line of fire at the original strafe speed (6 m/s) or runs at sprint speed (10 m/s),
    /// reversing every 1.5 s. Every aimed shot inside the drawn body should hit through lag compensation.
    /// </summary>
    [Theory]
    [InlineData(0, false, 0f)]
    [InlineData(50, false, 0f)]
    [InlineData(100, false, 0f)]
    [InlineData(150, false, 0f)]
    [InlineData(150, true, 0f)]
    [InlineData(0, true, 0.3f)]
    [InlineData(100, false, 0.3f)]
    [InlineData(150, true, -0.3f)]
    [InlineData(150, false, 0.3f)]
    // Controls: aiming beside the drawn body (0.8 m from its axis; radius 0.45) misses, so the test can fail.
    [InlineData(150, true, 0.8f)]
    [InlineData(100, false, -0.8f)]
    public void Shots_aimed_at_the_drawn_target_through_the_crosshair_hit(int rttMs, bool sprint, float lateral)
    {
        var config = OriginalSpeeds() with { HealthMax = 1e7f, ShieldMax = 1e7f, Damage = 22f, ShotCooldown = 0.133f, ClipSize = 1000, ReserveSize = 1000 };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(rttMs / 2.0), TimeSpan.FromMilliseconds(rttMs == 0 ? 0 : 5), 0);
        var h = new Harness(link, 21, 2, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        var log = new HitLog();
        h.Run(TimeSpan.FromSeconds(1.5), null, () => log.Observe(h.Server.World));
        var shooter = h.Clients[0]; var target = h.Clients[1];
        int sid = shooter.PlayerId, tid = target.PlayerId;
        var sPos = h.Server.World.Players[sid].Position;
        uint start = shooter.Tick + 30;
        var aimedAt = new Dictionary<uint, Vector3>();
        var camera = new Vector3(0.644f, -3.384f, 2.382f);   // right, forward, up of the character (CameraRig navigationSet_standing at 90°)

        PlayerInput Input(int i, uint t)
        {
            if (i == 1)
            {
                // Across the line of fire: strafing sideways, or running forward (sprint) while facing across it.
                var u = target.Predicted.Position - sPos;
                float facing = MathF.Atan2(-u.X, u.Y);
                float dir = (t / 45) % 2 == 0 ? 1f : -1f;
                return sprint ? new PlayerInput(t, 0, 1f, facing + dir * MathF.PI / 2, InputButtons.Sprint) : new PlayerInput(t, dir, 0, facing, InputButtons.None);
            }
            if (t < start) return new PlayerInput(t, 0, 0, 0, InputButtons.None);
            var seen = shooter.RemotePlayers().Players.Where(p => p.PlayerId == tid).ToList();
            if (seen.Count == 0) return new PlayerInput(t, 0, 0, 0, InputButtons.None);
            var drawn = seen[0];
            var v = new Vector3(drawn.Velocity.X, drawn.Velocity.Y, 0);
            var side = v.LengthSquared() > 0.01f ? Vector3.Normalize(v) : Vector3.Zero;
            var point = drawn.Position + new Vector3(0, 0, 1.0f) + side * lateral;
            var me = shooter.Predicted;
            // The camera follows the previous view yaw (close enough: the target moves little per tick).
            var toPoint = point - me.Position;
            float yaw0 = MathF.Atan2(-toPoint.X, toPoint.Y);
            var forward = new Vector3(-MathF.Sin(yaw0), MathF.Cos(yaw0), 0); var right = new Vector3(forward.Y, -forward.X, 0);
            var cam = me.Position + right * camera.X + forward * camera.Y + new Vector3(0, 0, camera.Z);
            var ray = point - cam;
            float yaw = MathF.Atan2(-ray.X, ray.Y), pitch = MathF.Atan2(ray.Z, new Vector2(ray.X, ray.Y).Length());
            aimedAt[t] = point;
            // Fire along the camera ray, from its point nearest the eye (PlayerInput.WithShot).
            var eye = Combat.EyePosition(me, config);
            return new PlayerInput(t, 0, 0, yaw, InputButtons.Fire, pitch).WithShot(NearestOnRay(cam, ray, eye), ray, eye);
        }

        var shots = new List<uint>();
        h.Run(TimeSpan.FromSeconds(10), Input, () =>
        {
            log.Observe(h.Server.World);
            foreach (var s in h.Server.World.LastShots) if (s.Shooter == sid && (shots.Count == 0 || shots[^1] != s.Tick)) shots.Add(s.Tick);
        });
        var hitTicks = log.Hits.Where(x => x.Shooter == sid && x.Target == tid).Select(x => x.Tick).ToHashSet();
        int hits = shots.Count(hitTicks.Contains);
        double rate = shots.Count == 0 ? 0 : hits / (double)shots.Count;
        float speed = new Vector2(h.Server.World.Players[tid].Velocity.X, h.Server.World.Players[tid].Velocity.Y).Length();
        output.WriteLine($"RTT {rttMs} ms, {(sprint ? "sprint" : "strafe")} (now {speed:F1} m/s), aim {lateral:+0.0;-0.0;0} m along the motion: {hits}/{shots.Count} hits ({100 * rate:F1}%)");
        Assert.True(shots.Count >= 50, $"only {shots.Count} shots");
        if (MathF.Abs(lateral) > config.BodyRadius + 0.2f) Assert.True(rate <= 0.05, $"control: aiming {lateral} m off the body still hit {100 * rate:F1}%");
        else Assert.True(rate >= (lateral == 0f ? 0.99 : 0.97), $"hit rate {100 * rate:F1}%");
    }
}
