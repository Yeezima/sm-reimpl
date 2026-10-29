using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M14: the jump pack (take-off, hold, flight, landing, power) and the ground pound (dive, impact blast, recovery), its
/// blocked actions, prediction exactness, protocol 9 and the config hash. Synthetic specs shaped like the install's values.
/// </summary>
public class JumpPackTests(ITestOutputHelper output)
{
    const int Assault = 0, Tactical = 1;
    const float Dt = 1f / 30f;

    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    internal static readonly SplashSpec PoundBlast = new(7f, Plain with { Damage = 25f, ShieldDamage = 50f }, [new(0f, 1f), new(4.5f, 1f), new(7f, 0.05f)], true, true, "target_all");
    internal static readonly JumpPackSpec Pack = new("pack", [new(0.1f, 4f), new(0.1f, 4f), new(0.25f, 13f), new(0.4f, 18f)],
        [new(0.1f, 35f), new(0.25f, 35f), new(0.4f, 55f)], 100f, 10f, 20f, 12f, 0.5f, 30f, 16f, 0.02f, 0.01f)
    {
        PoundEnabled = true, PoundDelay = 0f, PoundGravity = 75f, PoundSpeed = 50f, PoundLimitAngle = 50f, Pound = PoundBlast,
        Landings = [new(0f, new SplashSpec(4f, Plain with { Damage = 5f }, [], true, true, "target_all")),
                    new(15f, new SplashSpec(4f, Plain with { Damage = 15f }, [new(3f, 1f), new(4f, 0.1f)], true, true, "target_all"))],
    };
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.2f, 10, 50, 1.3f, 100f, 100f, 100f, 1f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 20f, 0.15f, 30, 270, 1.5f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);

    internal static readonly SimConfig Config = new()
    {
        Weapons = [Pistol, Rifle],
        Classes = [new ClassLoadout("assault", -1, 0, SimConfig.PlaceholderMeleeSteps) { JumpPack = 0 }, new ClassLoadout("tactical", 1, 0, SimConfig.PlaceholderMeleeSteps)],
        JumpPacks = [Pack],
    };

    static PlayerState Fresh(SimConfig? c = null, int cls = Assault, Vector3 at = default) =>
        Combat.Respawned(new PlayerState(1, at, Vector3.Zero, 0f, true) { Class = cls }, c ?? Config);

    static SelfEvents Step(ref PlayerState s, InputButtons b, SimConfig? c = null, float mx = 0f, float my = 0f, float pitch = 0f, CollisionWorld? world = null, float yaw = 0f)
    {
        s = Combat.StepPlayer(s, new PlayerInput(0, mx, my, yaw, b, pitch), c ?? Config, out var e, world);
        return e;
    }

    // ---------------------------------------------------------------- take-off, hold and power

    [Fact]
    public void A_press_edge_starts_the_take_off_and_the_lift_off_comes_at_the_take_off_delay()
    {
        var s = Fresh();
        Assert.Equal(100f, s.JumpPower);
        Step(ref s, InputButtons.Jump);
        Assert.Equal((JumpPhase.TakeOff, 0, 65f), (s.Jump, s.JumpTicks, s.JumpPower));
        for (int k = 1; k < 6; k++) { Assert.Equal(JumpEventKind.None, Step(ref s, InputButtons.None).Jump); Assert.Equal(JumpPhase.TakeOff, s.Jump); Assert.True(s.Grounded); }
        var e = Step(ref s, InputButtons.None);   // 6 ticks = 0.2 s after the press
        Assert.Equal((JumpEventKind.Launch, JumpPhase.Ascent), (e.Jump, s.Jump));
        Assert.False(s.Grounded);
        Assert.Equal(MathF.Sqrt(2f * 20f * 4f) - 20f * Dt, s.Velocity.Z, 4);   // a tap: the first height point (4 m)
        Assert.Equal(65f, s.JumpPower);   // no recharge in the air
        // Holding Jump through the landing does not start another jump: only a press edge does.
        for (int k = 0; k < 200 && s.Jump != JumpPhase.Landed; k++) Step(ref s, InputButtons.Jump);
        Assert.Equal(JumpPhase.Landed, s.Jump);
        for (int k = 0; k < 30; k++) Step(ref s, InputButtons.Jump);
        Assert.Equal(JumpPhase.None, s.Jump);
    }

    static float Apex(PlayerState s, Func<int, InputButtons> buttons, out PlayerState after, out int ticks)
    {
        float apex = 0f;
        ticks = 0;
        for (int k = 0; k < 400; k++)
        {
            Step(ref s, buttons(k));
            apex = MathF.Max(apex, s.Position.Z);
            ticks++;
            if (s.Jump == JumpPhase.Landed) break;
        }
        after = s;
        return apex;
    }

    [Theory]
    [InlineData(1, 4f, 35f)]      // tap
    [InlineData(4, 4f, 35f)]      // held 0.1 s (the hold counts from the press tick: 3 ticks)
    [InlineData(8, 12f, 35f)]     // 7 ticks = 0.2333 s: 4 + 9 × (0.1333 / 0.15)
    [InlineData(13, 18f, 55f)]    // 12 ticks = 0.4 s: the most
    [InlineData(40, 18f, 55f)]    // longer holds add nothing
    public void The_hold_sets_the_height_and_the_cost(int heldTicks, float height, float cost)
    {
        var s = Fresh();
        float apex = Apex(s, k => k < heldTicks ? InputButtons.Jump : InputButtons.None, out var after, out _);
        output.WriteLine($"held {heldTicks} ticks: apex {apex:F2} m (model {height}), power {after.JumpPower:F2}");
        // Discrete integration loses about half a tick of rise speed: within 0.6 m of the model height.
        Assert.InRange(apex, height - 0.6f, height + 0.05f);
        Assert.Equal(100f - cost, after.JumpPower, 3);
    }

    [Fact]
    public void Power_boundaries_the_cheapest_jump_needs_its_cost_and_power_recharges_only_on_the_ground()
    {
        var s = Fresh() with { JumpPower = 34.6f };
        Step(ref s, InputButtons.Jump);   // recharges first: 34.6 + 1/3 = 34.93 < 35
        Assert.Equal(JumpPhase.None, s.Jump);
        Assert.Equal(34.6f + 10f * Dt, s.JumpPower, 4);
        Step(ref s, InputButtons.None);
        Assert.Equal(JumpPhase.None, s.Jump);   // 35.27, but no press edge
        Step(ref s, InputButtons.Jump);         // 35.6: starts and spends 35
        Assert.Equal(JumpPhase.TakeOff, s.Jump);
        Assert.Equal(0.6f, s.JumpPower, 3);
        // Held: holds up to 0.25 s cost no more (35), the next tick's extra cost (to 37.2) is not covered, so the hold
        // stops at 7 ticks (0.2333 s: 12 m) and nothing more is spent.
        float apex = Apex(s, _ => InputButtons.Jump, out var after, out _);
        Assert.InRange(apex, 11.4f, 12.05f);
        Assert.Equal(0.6f, after.JumpPower, 3);
        // On the ground it recharges at 10 per second up to the maximum.
        var g = Fresh() with { JumpPower = 90f };
        for (int k = 0; k < 60; k++) Step(ref g, InputButtons.None);
        Assert.Equal(100f, g.JumpPower);
    }

    [Fact]
    public void A_new_jump_after_a_landing_waits_for_the_landing_jump_lock()
    {
        var s = Fresh();
        Apex(s, k => k == 0 ? InputButtons.Jump : InputButtons.None, out s, out _);
        Assert.Equal((JumpPhase.Landed, false), (s.Jump, s.JumpLandMoving));
        // Idle landing: jump lock 0.277 s = 8.3 ticks; the landing tick is tick 0, presses before tick 9 do nothing.
        var probe = s;
        for (int k = 1; k <= 8; k++) { Step(ref probe, (k % 2 == 1) ? InputButtons.Jump : InputButtons.None); Assert.NotEqual(JumpPhase.TakeOff, probe.Jump); }
        var late = s;
        for (int k = 1; k <= 8; k++) Step(ref late, InputButtons.None);
        Step(ref late, InputButtons.Jump);   // tick 9: 0.3 s
        Assert.Equal(JumpPhase.TakeOff, late.Jump);
    }

    // ---------------------------------------------------------------- flight

    [Fact]
    public void The_flight_follows_the_model_gravity_by_phase_forward_speed_and_air_control()
    {
        var s = Fresh();
        var phases = new List<(JumpPhase Phase, float Vz)>();
        for (int k = 0; k < 400 && s.Jump != JumpPhase.Landed; k++)
        {
            Step(ref s, k < 20 ? InputButtons.Jump : InputButtons.None, my: 1f);
            phases.Add((s.Jump, s.Velocity.Z));
        }
        int launch = phases.FindIndex(p => p.Phase == JumpPhase.Ascent);
        Assert.Equal(6, launch);
        // Lift-off: full stick forward at yaw 0 = 16 m/s along +Y; air control keeps the wished 16 m/s.

        var deltas = phases.Zip(phases.Skip(1)).Where(p => p.First.Phase == p.Second.Phase).ToList();
        void Gravity(JumpPhase phase, float g)
        {
            var d = deltas.Where(p => p.First.Phase == phase && p.Second.Phase == phase).Select(p => p.Second.Vz - p.First.Vz).ToList();
            Assert.NotEmpty(d);
            Assert.All(d, v => Assert.Equal(-g * Dt, v, 3));
        }
        Gravity(JumpPhase.Hang, 12f);
        Gravity(JumpPhase.Fall, 30f);
        // Ascent after the hold stopped growing (tick 6 + 6 = 12 is the last hold tick): plain jumping gravity.
        var rising = phases.Skip(13).TakeWhile(p => p.Phase == JumpPhase.Ascent).ToList();
        Assert.All(rising.Zip(rising.Skip(1)), p => Assert.Equal(-20f * Dt, p.Second.Vz - p.First.Vz, 3));
        // The apex tick (first Hang state) still used jumping gravity; the 15 ticks after it (0.5 s) use hang gravity, then fall gravity.
        int apex = phases.FindIndex(p => p.Phase == JumpPhase.Hang);
        for (int k = apex + 1; k <= apex + 15; k++) Assert.Equal(-12f * Dt, phases[k].Vz - phases[k - 1].Vz, 3);
        Assert.Equal(JumpPhase.Fall, phases[apex + 16].Phase);
        Assert.Equal(-30f * Dt, phases[apex + 16].Vz - phases[apex + 15].Vz, 3);
        Assert.Equal(16, phases.Count(p => p.Phase == JumpPhase.Hang));   // the apex tick and the 15 hang-gravity ticks
        Assert.True(s.JumpLandMoving);
        Assert.InRange(s.Position.Y, 16f * 2.5f, 16f * 4f);   // forward the whole flight at 16 m/s
    }

    [Fact]
    public void Air_control_closes_the_gain_of_the_gap_per_tick_and_drifts_without_stick()
    {
        var s = Fresh();
        for (int k = 0; k < 7; k++) Step(ref s, InputButtons.Jump, my: 1f);   // lifted off forward
        Assert.Equal(JumpPhase.Ascent, s.Jump);
        float vy = s.Velocity.Y;
        Assert.Equal(16f, vy, 4);
        Step(ref s, InputButtons.Jump, mx: 1f);   // full right: wished (16, 0)
        Assert.Equal(16f * 0.02f, s.Velocity.X, 4);
        Assert.Equal(16f * 0.98f, s.Velocity.Y, 4);
        float before = s.Velocity.Y;
        Step(ref s, InputButtons.Jump);           // released: drifts toward 0 by 1 %
        Assert.Equal(before * 0.99f, s.Velocity.Y, 4);
    }

    [Fact]
    public void A_player_lands_on_map_collision_and_the_landing_locks_movement()
    {
        // Ground at z = 0 and a 5 m high block from y = 20 to 60.
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        Quad(new(-60, -60, 0), new(60, -60, 0), new(60, 20, 0), new(-60, 20, 0));
        Quad(new(-60, 20, 5), new(60, 20, 5), new(60, 60, 5), new(-60, 60, 5));
        Quad(new(-60, 20, 0), new(60, 20, 0), new(60, 20, 5), new(-60, 20, 5));
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        var world = new CollisionWorld(mesh, mesh);
        var s = Fresh();
        for (int k = 0; k < 400 && s.Jump != JumpPhase.Landed; k++) Step(ref s, k < 20 ? InputButtons.Jump : InputButtons.None, my: 1f, world: world);
        Assert.Equal(JumpPhase.Landed, s.Jump);
        Assert.Equal(5f, s.Position.Z, 2);
        Assert.True(s.Position.Y > 20f);
        // Moving landing: movement input ignored for 0.333 s (the stick slows the player like no input), then it returns.
        var locked = s;
        for (int k = 1; k <= 9; k++) Step(ref locked, InputButtons.None, mx: 1f, world: world);
        Assert.True(MathF.Abs(locked.Velocity.X) < 1e-4f, $"moved sideways during the lock: {locked.Velocity}");
        Step(ref locked, InputButtons.None, mx: 1f, world: world);   // 10 ticks = 0.333 s
        Assert.True(locked.Velocity.X < -0.1f || locked.Velocity.X > 0.1f);
    }

    // ---------------------------------------------------------------- ground pound

    [Fact]
    public void The_dive_starts_on_melee_in_flight_leaned_at_most_the_limit_and_the_impact_locks_the_player()
    {
        var s = Fresh();
        for (int k = 0; k < 16; k++) Step(ref s, InputButtons.Jump);   // well into the ascent
        Assert.Equal(JumpPhase.Ascent, s.Jump);
        var e = Step(ref s, InputButtons.Melee, pitch: 0f);   // aiming level: leaned 50° from down
        Assert.Equal((JumpEventKind.Dive, JumpPhase.Dive, 0), (e.Jump, s.Jump, s.MeleeStep));
        Assert.Equal(-50f * MathF.Cos(50f * MathF.PI / 180f) - 75f * Dt, s.Velocity.Z, 3);
        Assert.Equal(50f * MathF.Sin(50f * MathF.PI / 180f), s.Velocity.Y, 3);
        int dive = 0;
        while (s.Jump == JumpPhase.Dive && dive++ < 100) e = Step(ref s, InputButtons.Fire | InputButtons.Switch, mx: 1f);
        Assert.Equal((JumpEventKind.Impact, JumpPhase.Recover, Vector3.Zero), (e.Jump, s.Jump, s.Velocity));
        Assert.Equal(10, s.Clip);   // fire and switch waited during the dive
        Assert.Equal(WeaponSlot.Pistol, s.Slot);
        // Recovery 0.356 s: ticks 1..10 recovering (no movement, no fire), tick 11 (0.367 s) free.
        for (int k = 1; k <= 10; k++)
        {
            Step(ref s, k % 2 == 1 ? InputButtons.Fire : InputButtons.None, my: 1f);
            Assert.Equal(JumpPhase.Recover, s.Jump);
        }
        Assert.Equal((Vector3.Zero, 10), (s.Velocity, s.Clip));
        Step(ref s, InputButtons.Fire, my: 1f);
        Assert.Equal(JumpPhase.None, s.Jump);
        Assert.Equal(9, s.Clip);
        Assert.True(s.Velocity.Y > 0f);
    }

    [Fact]
    public void Straight_down_aim_dives_along_the_aim_and_a_pound_cannot_start_on_the_ground_or_without_the_ability()
    {
        Assert.True(Vector3.Distance(new Vector3(0, 0, -1), JumpPack.DiveDirection(0f, -MathF.PI / 2, 50f)) < 1e-5f);
        var d = JumpPack.DiveDirection(0f, -1.2f, 50f);   // 68.8° below level = 21.2° from down: within the limit
        Assert.Equal(Combat.AimDirection(0f, -1.2f), d);
        var g = Fresh();
        Step(ref g, InputButtons.Melee);
        Assert.Equal((JumpPhase.None, 1), (g.Jump, g.MeleeStep));   // on the ground melee is melee
        var off = Config with { JumpPacks = [Pack with { PoundEnabled = false }] };
        var s = Fresh(off);
        for (int k = 0; k < 7; k++) Step(ref s, InputButtons.Jump, off);
        Step(ref s, InputButtons.Melee, off);
        Assert.Equal((JumpPhase.Ascent, 0), (s.Jump, s.MeleeStep));   // no pound, and no melee attack in the air
        // During the take-off melee presses do nothing either.
        var t = Fresh();
        Step(ref t, InputButtons.Jump);
        Step(ref t, InputButtons.Melee);
        Assert.Equal((JumpPhase.TakeOff, 0), (t.Jump, t.MeleeStep));
    }

    [Fact]
    public void The_pistol_fires_in_flight_and_a_melee_attack_in_progress_blocks_the_jump()
    {
        var s = Fresh();
        for (int k = 0; k < 9; k++) Step(ref s, InputButtons.Jump);
        Assert.Equal(JumpPhase.Ascent, s.Jump);
        Assert.True(Step(ref s, InputButtons.Fire).Fired);
        Assert.Equal(9, s.Clip);
        var m = Fresh();
        Step(ref m, InputButtons.Melee);
        Step(ref m, InputButtons.Jump);
        Assert.Equal((JumpPhase.None, 1), (m.Jump, m.MeleeStep));
        var both = Fresh();
        Step(ref both, InputButtons.Melee | InputButtons.Jump);   // the same tick: melee wins
        Assert.Equal((JumpPhase.None, 1), (both.Jump, both.MeleeStep));
    }

    [Fact]
    public void Classes_without_a_pack_cannot_jump_and_keep_canonical_jump_state()
    {
        var s = Fresh(cls: Tactical);
        Assert.Equal(0f, s.JumpPower);
        for (int k = 0; k < 30; k++) Step(ref s, k % 2 == 0 ? InputButtons.Jump : InputButtons.None, my: 1f);
        Assert.Equal((JumpPhase.None, 0, 0, 0f, 0f), (s.Jump, s.JumpTicks, s.JumpMark, s.JumpPower, s.Position.Z));
        Assert.Null(Loadouts.JumpPack(Config, Tactical));
        // A class index outside the pack list is no pack.
        var broken = Config with { Classes = [Config.Classes[0] with { JumpPack = 5 }] };
        Assert.Null(Loadouts.JumpPack(broken, 0));
    }

    [Fact]
    public void Death_ends_the_jump_and_a_respawn_refills_the_power()
    {
        var s = Fresh();
        for (int k = 0; k < 9; k++) Step(ref s, InputButtons.Jump);
        var dead = Combat.Killed(s, Config);
        Assert.Equal((JumpPhase.None, 0, false), (dead.Jump, dead.JumpTicks, dead.JumpHolding));
        Assert.Equal(100f, Combat.Respawned(dead with { JumpPower = 3f }, Config).JumpPower);
    }

    // ---------------------------------------------------------------- world: blasts

    static World WorldAt(SimConfig c, CollisionWorld? collision, params Vector3[] at)
    {
        var world = new World(c, collision, at.Select(p => new SpawnPoint(p, 0f)).ToArray());
        world.Spawn(1, Assault);
        for (int i = 1; i < at.Length; i++) world.Spawn(i + 1, Tactical);
        return world;
    }

    /// <summary>Player 1 jumps (tap), then pounds at the apex aiming straight down; returns the impact event.</summary>
    static JumpEvent PoundIn(World world, List<HitEvent> hits)
    {
        bool pounded = false;
        for (int k = 0; k < 300; k++)
        {
            var p = world.Players[1];
            var b = k == 0 ? InputButtons.Jump : !pounded && p.Jump == JumpPhase.Hang ? InputButtons.Melee : InputButtons.None;
            if (b == InputButtons.Melee) pounded = true;
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, b, -1.4f) : new PlayerInput(0, 0, 0, 0f, InputButtons.None));
            hits.AddRange(world.LastHits);
            if (world.LastJumps.FirstOrDefault(j => j.Kind == JumpEventKind.Impact) is { Player: 1 } impact) return impact;
        }
        throw new Xunit.Sdk.XunitException("no impact");
    }

    [Fact]
    public void The_pound_blast_falls_off_linearly_excludes_the_owner_and_stops_at_its_radius()
    {
        var world = WorldAt(Config, null, Vector3.Zero, new Vector3(2.5f, 0, 0), new Vector3(0, 6f, 0), new Vector3(-8f, 0, 0));
        var hits = new List<HitEvent>();
        var impact = PoundIn(world, hits);
        output.WriteLine($"impact at {impact.Position}, drop {impact.Drop:F2}: {impact.Victims} hit, {impact.ShieldDamage:F1} + {impact.HealthDamage:F1}");
        var blast = hits.Where(h => h.Landing).ToList();
        Assert.DoesNotContain(blast, h => h.Target == 1);   // ignore_self
        Assert.DoesNotContain(blast, h => h.Target == 4);   // 8 m: outside 7 m
        Assert.Equal(2, impact.Victims);
        foreach (var h in blast)
        {
            var victim = world.Players[h.Target];
            float distance = Vector2.Distance(new(impact.Position.X, impact.Position.Y), new(victim.Position.X, victim.Position.Y));
            Assert.Equal(distance, h.Distance, 3);
            float intensity = Combat.SplashIntensity(PoundBlast.FallOff, distance);
            Assert.Equal((50f + 25f) * intensity, h.ShieldDamage, 2);   // shield-only part, then the damage (shield ×1)
            Assert.Equal(0f, h.HealthDamage);
            output.WriteLine($"  player {h.Target} at {distance:F2} m: intensity {intensity:F3}, shield {h.ShieldDamage:F2}");
        }
        Assert.Contains(blast, h => h.Target == 2 && h.ShieldDamage == 75f);    // within 4.5 m: full
        Assert.Contains(blast, h => h.Target == 3 && h.ShieldDamage < 75f);     // beyond 4.5 m: less
        Assert.Equal(100f, world.Players[1].Shield);
    }

    [Fact]
    public void A_wall_between_the_impact_and_a_player_blocks_the_blast()
    {
        // Ground and a wall at x = 3 (control player on the open side).
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        Quad(new(-60, -60, 0), new(60, -60, 0), new(60, 60, 0), new(-60, 60, 0));
        Quad(new(3, -60, -1), new(3, 60, -1), new(3, 60, 30), new(3, -60, 30));
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        var world = WorldAt(Config, new CollisionWorld(mesh, mesh), new Vector3(0, 0, 0), new Vector3(4.5f, 0, 0), new Vector3(-2f, 0, 0));
        var hits = new List<HitEvent>();
        PoundIn(world, hits);
        Assert.DoesNotContain(hits, h => h.Target == 2);
        Assert.Contains(hits, h => h.Target == 3 && h.Landing);
    }

    [Fact]
    public void A_regular_landing_applies_the_blast_for_its_drop()
    {
        var world = WorldAt(Config, null, Vector3.Zero, new Vector3(1f, 0, 0));
        var hits = new List<HitEvent>();
        JumpEvent? land = null;
        for (int k = 0; k < 300 && land is null; k++)
        {
            world.Step(id => new PlayerInput(0, 0, 0, 0f, id == 1 && k == 0 ? InputButtons.Jump : InputButtons.None));
            hits.AddRange(world.LastHits);
            land = world.LastJumps.Where(j => j.Kind == JumpEventKind.Land).Cast<JumpEvent?>().FirstOrDefault();
        }
        Assert.NotNull(land);
        Assert.InRange(land!.Value.Drop, 3f, 4.05f);   // a tap: under 15 m, the 5-damage blast
        var h = Assert.Single(hits, x => x.Landing);
        Assert.Equal((2, 5f), (h.Target, h.ShieldDamage));
        // A full jump drops more than 15 m: the 15-damage blast with its fall-off (1 m: full).
        var high = WorldAt(Config, null, Vector3.Zero, new Vector3(1f, 0, 0));
        hits.Clear(); land = null;
        for (int k = 0; k < 400 && land is null; k++)
        {
            high.Step(id => new PlayerInput(0, 0, 0, 0f, id == 1 && k < 20 ? InputButtons.Jump : InputButtons.None));
            hits.AddRange(high.LastHits);
            land = high.LastJumps.Where(j => j.Kind == JumpEventKind.Land).Cast<JumpEvent?>().FirstOrDefault();
        }
        Assert.True(land!.Value.Drop >= 15f, $"drop {land.Value.Drop}");
        Assert.Equal(15f, Assert.Single(hits, x => x.Landing).ShieldDamage);
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(141)]
    [InlineData(142)]
    public void Prediction_matches_the_server_while_jumping_and_pounding_under_150ms_rtt_and_5pct_loss(int seed)
    {
        static SplashSpec Harmless(SplashSpec s) => s with { Damage = s.Damage with { Damage = 0f, ShieldDamage = 0f } };
        var config = Config with
        {
            JumpPacks = [Pack with { Pound = Harmless(Pack.Pound!), Landings = Pack.Landings.Select(l => l with { Blast = Harmless(l.Blast) }).ToArray() }],
            Weapons = [Pistol with { Damage = 0f, ReserveSize = 5000 }, Rifle with { Damage = 0f }],
            Classes = Config.Classes.Select(c => c with { Melee = c.Melee.Select(m => m with { Damage = 0f, SecondaryDamage = 0f }).ToArray() }).ToArray(),
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        for (int i = 0; i < 3; i++) h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = (byte)(i < 2 ? Assault : Tactical) });
        int n = h.Clients.Count;
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, n).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons & ~(InputButtons.Jump | InputButtons.Sprint);
            uint phase = (t + (uint)i * 11) % 150;
            if (phase < 2 + (uint)i * 5) buttons |= InputButtons.Jump;                              // holds of 2, 7, 12 ticks
            if (phase is 40 or 41 && i == 0) buttons |= InputButtons.Melee;                         // a pound near the apex
            if (phase is 20 or 21 or 70 or 71) buttons |= InputButtons.Fire;                        // in flight and on the ground
            if (phase is 100 or 101 or 120) buttons |= InputButtons.Jump;                            // a second jump, presses in the landing lock
            if (phase is 110 or 111) buttons |= InputButtons.Melee;
            return move with { Buttons = buttons, Pitch = -0.6f };
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < n; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        for (int i = 0; i < n; i++)
        {
            var c = h.Clients[i];
            int id = c.PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            int launches = mine.Zip(mine.Skip(1)).Count(p => p.First.Jump == JumpPhase.TakeOff && p.Second.Jump == JumpPhase.Ascent);
            int pounds = mine.Zip(mine.Skip(1)).Count(p => p.First.Jump != JumpPhase.Dive && p.Second.Jump == JumpPhase.Dive);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches; {launches} lift-offs, {pounds} pounds");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            if (i < 2) Assert.True(launches >= 6, $"client {i}: only {launches} lift-offs");
            else Assert.Equal(0, launches);
            if (i == 0) Assert.True(pounds >= 5, $"only {pounds} pounds");
            Assert.Empty(mismatches);
        }
    }

    // ---------------------------------------------------------------- review additions: collision, throws, boundaries

    static CollisionWorld Mesh(params (Vector3 A, Vector3 B, Vector3 C, Vector3 D)[] quads)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        foreach (var (a, b, c, d) in quads) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    static (Vector3, Vector3, Vector3, Vector3) Floor(float z, float x0 = -300, float x1 = 300, float y0 = -300, float y1 = 300) =>
        (new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z));

    static readonly ThrownSpec Frag = new("frag", 2, 2, 1f, 10f, new ProjectileSpec("ebps\\test\\frag", 20f, 3f, "raycast", 0f, [], true, true, false, 0, 0f, 0f, 0, null,
        new SplashSpec(3f, Plain with { Damage = 1f }, [], true, false, "target_all")));

    [Fact]
    public void A_dive_cancels_an_unreleased_throw_and_spends_nothing()
    {
        var c = Config with { Thrown = [Frag], Classes = [Config.Classes[0] with { EquipmentChoices = [0] }, Config.Classes[1]] };
        PlayerState InHang()
        {
            var s = Fresh(c);
            Step(ref s, InputButtons.Jump, c);
            for (int k = 0; k < 100 && s.Jump != JumpPhase.Hang; k++) Step(ref s, InputButtons.None, c);
            Assert.Equal(JumpPhase.Hang, s.Jump);
            return s;
        }
        var p = InHang();
        Step(ref p, InputButtons.Grenade, c);
        Assert.True(p.Throwing);
        var e = Step(ref p, InputButtons.Melee, c);   // 1 tick into the throw (release at 0.2 s)
        Assert.Equal((JumpEventKind.Dive, false, 2), (e.Jump, p.Throwing, p.Grenades));
        bool threw = false;
        for (int k = 0; k < 40; k++) threw |= Step(ref p, InputButtons.None, c).Threw;
        Assert.False(threw);
        Assert.Equal(2, p.Grenades);
        // Control: without the pound the throw releases in the air.
        var q = InHang();
        Step(ref q, InputButtons.Grenade, c);
        threw = false;
        for (int k = 0; k < 10; k++) threw |= Step(ref q, InputButtons.None, c).Threw;
        Assert.True(threw);
        Assert.Equal(1, q.Grenades);
    }

    [Fact]
    public void Leaving_the_ground_during_the_take_off_turns_the_jump_into_a_fall_with_the_power_spent()
    {
        // A 5 m ledge ending at y = 0.5, the floor below.
        var level = Mesh(Floor(5f, y1: 0.5f), Floor(0f, y0: 0.5f), (new(-300, 0.5f, 0), new(300, 0.5f, 0), new(300, 0.5f, 5), new(-300, 0.5f, 5)));
        var s = Fresh(at: new Vector3(0, 0.3f, 5f)) with { Velocity = new Vector3(0, 5f, 0) };
        var events = new List<JumpEventKind>();
        float top = 0f;
        for (int k = 0; k < 120 && s.Jump != JumpPhase.Landed; k++)
        {
            events.Add(Step(ref s, k == 0 ? InputButtons.Jump : InputButtons.None, my: 1f, world: level).Jump);
            top = MathF.Max(top, s.Position.Z);
            if (k == 6) Assert.Equal(JumpPhase.Fall, s.Jump);
        }
        Assert.DoesNotContain(JumpEventKind.Launch, events);
        Assert.True(top <= 5.001f, $"rose to {top}");
        Assert.Equal(JumpPhase.Landed, s.Jump);
        Assert.Equal(0f, s.Position.Z, 3);
        Assert.Equal(65f, s.JumpPower, 3);
        // Control: far from the edge it lifts off.
        var g = Fresh(at: new Vector3(0, -10f, 5f));
        for (int k = 0; k < 7; k++) Step(ref g, k == 0 ? InputButtons.Jump : InputButtons.None, world: level);
        Assert.Equal(JumpPhase.Ascent, g.Jump);
    }

    [Fact]
    public void A_ceiling_ends_the_ascent_without_clipping_and_starts_the_hang()
    {
        var level = Mesh(Floor(0f), Floor(6f));
        var s = Fresh();
        float top = 0f;
        bool hangBelow = false;
        for (int k = 0; k < 200 && s.Jump != JumpPhase.Landed; k++)
        {
            Step(ref s, InputButtons.Jump, world: level);
            top = MathF.Max(top, s.Position.Z);
            if (s.Jump == JumpPhase.Hang && !hangBelow) { hangBelow = true; Assert.True(s.Velocity.Z <= 0f); }
        }
        Assert.True(hangBelow);
        Assert.InRange(top + Config.CapsuleHeight, 5.5f, 6.01f);   // it reached the ceiling and stopped there (18 m without it)
        Assert.Equal(JumpPhase.Landed, s.Jump);
    }

    [Fact]
    public void A_dive_at_the_fall_speed_cap_lands_on_a_thin_floor_and_one_onto_a_steep_slope_slides_off_to_the_ground()
    {
        var thin = Mesh(Floor(0f, -20, 20, -20, 20));
        var s = Fresh() with { Position = new Vector3(0, 0, 60f), Grounded = false, Jump = JumpPhase.Fall, JumpTicks = 60, JumpMark = 30, JumpPower = 50f };
        Step(ref s, InputButtons.Melee, pitch: -1.4f, world: thin);
        float fastest = 0f;
        JumpEventKind e = JumpEventKind.None;
        for (int k = 0; k < 100 && s.Jump == JumpPhase.Dive; k++) { e = Step(ref s, InputButtons.None, world: thin).Jump; if (s.Jump == JumpPhase.Dive) fastest = MathF.Max(fastest, -s.Velocity.Z); }
        Assert.Equal((JumpEventKind.Impact, JumpPhase.Recover), (e, s.Jump));
        Assert.Equal(Config.MaxFallSpeed, fastest, 3);   // capped on a map
        Assert.Equal(0f, s.Position.Z, 3);
        // A 69° slope (x 0..3 rising 8 m), the floor at x < 0: the dive slides down it and the impact comes on the floor.
        var slope = Mesh(Floor(0f, x1: 0f), (new(0, -300, 0), new(0, 300, 0), new(3, 300, 8), new(3, -300, 8)), Floor(8f, x0: 3f));
        var d = Fresh() with { Position = new Vector3(1.5f, 0, 15f), Grounded = false, Jump = JumpPhase.Fall, JumpTicks = 60, JumpMark = 30, JumpPower = 50f };
        Step(ref d, InputButtons.Melee, pitch: -1.4f, world: slope);
        for (int k = 0; k < 200 && d.Jump == JumpPhase.Dive; k++) Step(ref d, InputButtons.None, world: slope);
        output.WriteLine($"slope dive ended at {d.Position} in {d.Jump}");
        Assert.Equal(JumpPhase.Recover, d.Jump);
        Assert.True(d.Position.X < 0.5f && MathF.Abs(d.Position.Z) < 0.05f, $"{d.Position}");
    }

    [Fact]
    public void The_landing_blast_switches_at_exactly_its_minimum_drop()
    {
        Assert.Equal(5f, JumpPack.LandingBlast(Pack, 14.99f)!.Damage.Damage);
        Assert.Equal(15f, JumpPack.LandingBlast(Pack, 15f)!.Damage.Damage);
        Assert.Equal(15f, JumpPack.LandingBlast(Pack, 14.99995f)!.Damage.Damage);   // within the 1e-4 m tolerance
        Assert.Equal(5f, JumpPack.LandingBlast(Pack, 0f)!.Damage.Damage);
        Assert.Null(JumpPack.LandingBlast(Pack with { Landings = [Pack.Landings[1]] }, 14.9f));
        Assert.Null(JumpPack.LandingBlast(Pack with { Landings = [] }, 99f));
    }

    [Fact]
    public void A_dive_off_the_level_ends_in_the_kill_depth_with_no_impact()
    {
        var world = new World(Config, Mesh(Floor(0f, -2, 2, -2, 2)), [new SpawnPoint(Vector3.Zero, 0f)]);
        world.Spawn(1, Assault);
        var kinds = new List<JumpEventKind>();
        bool died = false;
        for (int k = 0; k < 600 && !died; k++)
        {
            var p = world.Players[1];
            var b = k < 20 ? InputButtons.Jump : p.Jump == JumpPhase.Hang ? InputButtons.Melee : InputButtons.None;
            world.Step(_ => new PlayerInput(0, 0, 1f, 0f, b, -1.4f));
            kinds.AddRange(world.LastJumps.Select(j => j.Kind));
            if (!world.Players[1].Alive) { died = true; Assert.Equal(JumpPhase.None, world.Players[1].Jump); }
        }
        Assert.True(died);
        Assert.Contains(JumpEventKind.Dive, kinds);
        Assert.DoesNotContain(JumpEventKind.Impact, kinds);
    }

    [Fact]
    public void A_jump_blast_without_ignore_self_hits_its_owner_too()
    {
        var c = Config with { JumpPacks = [Pack with { Pound = PoundBlast with { IgnoreSelf = false } }] };
        var world = WorldAt(c, null, Vector3.Zero, new Vector3(2.5f, 0, 0));
        var hits = new List<HitEvent>();
        PoundIn(world, hits);
        Assert.Contains(hits, h => h.Target == 1 && h.Landing && h.ShieldDamage > 0f);
        Assert.Contains(hits, h => h.Target == 2 && h.Landing);
    }

    [Fact]
    public void The_loopback_network_refuses_packets_larger_than_a_udp_packet()
    {
        var net = new LoopbackNetwork(new ManualClock(), new LinkConditions(TimeSpan.Zero, TimeSpan.Zero, 0));
        var client = net.Connect();
        client.Send(new byte[UdpServerTransport.MaxPacket]);   // control
        Assert.Throws<InvalidOperationException>(() => client.Send(new byte[UdpServerTransport.MaxPacket + 1]));
        Assert.Throws<InvalidOperationException>(() => net.Server.Send(0, new byte[UdpServerTransport.MaxPacket + 1]));
    }

    [Theory]
    [InlineData(151)]
    [InlineData(152)]
    public void Prediction_matches_the_server_on_a_map_with_a_ceiling_and_ledges_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // Floor; a ceiling at 7 m over x < 5 (the first spawn is under it); a 3 m platform for x in [10, 40] (ledges to step off and walls to hit).
        var level = Mesh(Floor(0f), Floor(7f, x0: -300, x1: 5), Floor(3f, 10, 40, -300, 300),
            (new(10, -300, 0), new(10, 300, 0), new(10, 300, 3), new(10, -300, 3)), (new(40, -300, 0), new(40, 300, 0), new(40, 300, 3), new(40, -300, 3)));
        var map = new GameMap("jp_test", level, [new SpawnPoint(new Vector3(0, 0, 0), 0f), new SpawnPoint(new Vector3(15, 0, 3), 1f), new SpawnPoint(new Vector3(-15, 0, 0), 2f)], 77);
        static SplashSpec Harmless(SplashSpec s) => s with { Damage = s.Damage with { Damage = 0f, ShieldDamage = 0f } };
        var config = Config with
        {
            JumpPacks = [Pack with { Pound = Harmless(Pack.Pound!), Landings = Pack.Landings.Select(l => l with { Blast = Harmless(l.Blast) }).ToArray() }],
            Weapons = [Pistol with { Damage = 0f, ReserveSize = 5000 }, Rifle with { Damage = 0f }],
            Classes = Config.Classes.Select(c => c with { Melee = c.Melee.Select(m => m with { Damage = 0f, SecondaryDamage = 0f }).ToArray() }).ToArray(),
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) }, map: map);
        for (int i = 0; i < 3; i++)
            h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = (byte)(i < 2 ? Assault : Tactical), LoadMap = _ => new LoadedMap(level, 77) });
        int n = h.Clients.Count;
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, n).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons & ~(InputButtons.Jump | InputButtons.Sprint);
            uint phase = (t + (uint)i * 11) % 150;
            if (phase < (i == 0 ? 13u : 2u + (uint)i * 6)) buttons |= InputButtons.Jump;   // client 0: full holds (under the ceiling)
            if (phase is 35 or 36) buttons |= InputButtons.Melee;
            if (phase is 20 or 21) buttons |= InputButtons.Fire;
            if (phase is 80 or 81) buttons |= InputButtons.Jump;                                     // lands before the next cycle
            return move with { Buttons = buttons, Pitch = -0.7f };
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < n; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        for (int i = 0; i < n; i++)
        {
            int id = h.Clients[i].PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            int launches = mine.Zip(mine.Skip(1)).Count(p => p.First.Jump == JumpPhase.TakeOff && p.Second.Jump == JumpPhase.Ascent);
            int pounds = mine.Zip(mine.Skip(1)).Count(p => p.First.Jump != JumpPhase.Recover && p.Second.Jump == JumpPhase.Recover);
            float highest = mine.Max(p => p.Position.Z);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches; {launches} lift-offs, {pounds} pound impacts, highest {highest:F1} m, x range {mine.Min(p => p.Position.X):F0}..{mine.Max(p => p.Position.X):F0}");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            if (i < 2) { Assert.True(launches >= 5, $"client {i}: only {launches} lift-offs"); Assert.True(pounds >= 3, $"client {i}: only {pounds} impacts"); }
            if (i == 0) Assert.Contains(mine, p => p.Position.X < 4.5f && p.Position.Z < 7f && p.Position.Z + config.CapsuleHeight > 6.9f);   // client 0 reached the ceiling from below
            Assert.Empty(mismatches);
        }
    }

    // ---------------------------------------------------------------- protocol and hash

    [Fact]
    public void Protocol_9_carries_the_jump_state_in_full_and_flags_in_slim_records_within_1200_bytes()
    {
        Assert.Equal(43, Protocol.Version);
        PlayerState Loaded(int id) => new(id, new Vector3(1e4f), new Vector3(100f), 3f, false)
        {
            Alive = true, Health = 1e3f, Shield = 1e3f, Class = Protocol.MaxClasses - 1, Slot = WeaponSlot.Pistol, Clip = 65535, Reserve = 65535, OtherClip = 65535, OtherReserve = 65535,
            Primary = 254, Pistol = 253, MeleeChoice = 254, MeleeStep = Protocol.MaxMeleeStep, Equipment = 254, Grenades = 255, PendingShot = 255, ProjectilesOut = 255,
            Jump = (JumpPhase)(id % 8), JumpTicks = 65535, JumpMark = 65534, JumpPower = 55.25f, JumpHeld = true, JumpHolding = true, JumpLandMoving = true,
            MeleeQueued = true, MeleeHeld = true, FireHeld = true, SwitchHeld = true, Overheated = true, OtherOverheated = true,
            ReloadHeld = true, DetonateHeld = true, Charging = true, Throwing = true, GrenadeHeld = true,
        };
        var players = Enumerable.Range(1, Protocol.MaxPlayers).Select(Loaded).ToArray();
        var projectiles = Enumerable.Range(0, Protocol.MaxSnapshotProjectiles).Select(k => new ProjectileView(60000 + k, 254, 255, new Vector3(k), ProjectileFlags.Detonated)).ToArray();
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, players, Self: 7, Projectiles: projectiles, Match: new MatchView(MatchState.Playing, Team.None, uint.MaxValue, 65535, 65535), Kills: [.. Enumerable.Range(0, Protocol.MaxSnapshotKills).Select(k => new KillView(65535 - k, 65535, 1, KillCause.Thrown, 254))],
            Points: [.. Enumerable.Repeat(new ControlPointView(Team.Chaos, Team.Chaos, 1f, true), Protocol.MaxSnapshotPoints)], Shots: ShotTests.ManyShots(players, Protocol.MaxSnapshotShots)));
        output.WriteLine($"16 players and {projectiles.Length} projectiles (protocol {Protocol.Version}): {bytes.Length} bytes");
        Assert.Equal(Protocol.MaxSnapshotBytes, bytes.Length);   // protocol 16: 16 projectile records, the other blocks and the 5 shot records that fit
        Assert.True(bytes.Length <= UdpServerTransport.MaxPacket);
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal(players.Select(p => p.PlayerId == 7 ? p : Protocol.Slim(p)), back.Players);
        Assert.Equal(JumpPhase.Recover, back.Players[6].Jump);   // the full record (id 7): the exact phase
        // Slim records: a representative phase from the two flags.
        foreach (var p in back.Players.Where(p => p.PlayerId != 7))
        {
            var exact = (JumpPhase)(p.PlayerId % 8);
            var expected = JumpPack.Jumping(exact) ? JumpPhase.Ascent : JumpPack.Pounding(exact) ? JumpPhase.Dive : JumpPhase.None;
            Assert.Equal((expected, 0, 0f), (p.Jump, p.JumpTicks, p.JumpPower));
        }
        for (int k = 0; k < bytes.Length; k++) Assert.Null(Protocol.Read(bytes.AsSpan(0, k)));
    }

    [Fact]
    public void Protocol_9_refuses_unknown_jump_flags_and_records_whose_phase_disagrees_with_the_slim_flags()
    {
        var me = new PlayerState(3, Vector3.One, Vector3.Zero, 0f, false) { Alive = true, Jump = JumpPhase.Hang, JumpTicks = 40, JumpMark = 30, JumpPower = 45f, JumpHeld = true };
        var own = Protocol.Write(new Snapshot(1, 1, 1, [me], Self: 3));
        Assert.Equal(me, Assert.IsType<Snapshot>(Protocol.Read(own)).Players[0]);
        // The jump block sits before the 2 evade bytes (protocol 13), the 11 bytes of thrown equipment and the two flag bytes,
        // and those before the match block, kill count and point count (11 bytes since protocol 11) and the hit block (12
        // bytes since protocol 12): phase byte at [^56] (protocol 21: ready ticks in 2 bytes).
        Assert.Equal((byte)((int)JumpPhase.Hang | 8), own[^56]);
        var unknown = (byte[])own.Clone(); unknown[^56] |= 64;
        Assert.Null(Protocol.Read(unknown));
        var disagree = (byte[])own.Clone(); disagree[^56] = (byte)JumpPhase.Dive;   // slim flags say jumping
        Assert.Null(Protocol.Read(disagree));
        var control = (byte[])own.Clone(); control[^56] = (byte)JumpPhase.Fall;
        Assert.Equal(JumpPhase.Fall, Assert.IsType<Snapshot>(Protocol.Read(control)).Players[0].Jump);
        // Slim: both jump flags at once are refused; flag 128 is aiming since protocol 12.
        var slim = Protocol.Write(new Snapshot(1, 1, 1, [me with { PlayerId = 4 }]));
        Assert.Equal(32, slim[^35] & 96);   // the flags byte: 9 bytes of the slim record and the evade byte (protocol 13) follow it
        var both = (byte[])slim.Clone(); both[^35] |= 64;
        Assert.Null(Protocol.Read(both));
        var high = (byte[])slim.Clone(); high[^35] |= 128;
        Assert.True(Assert.IsType<Snapshot>(Protocol.Read(high)).Players[0].Aiming);
        var pound = Protocol.Write(new Snapshot(1, 1, 1, [me with { PlayerId = 4, Jump = JumpPhase.Recover }]));
        Assert.Equal(JumpPhase.Dive, Assert.IsType<Snapshot>(Protocol.Read(pound)).Players[0].Jump);
    }

    [Fact]
    public void The_config_hash_covers_the_jump_packs_and_the_classes_packs()
    {
        ulong h = Config.Hash();
        Assert.Equal(h, (Config with { JumpPacks = [Pack with { HeightMap = Pack.HeightMap.ToArray() }] }).Hash());
        var seen = new HashSet<ulong> { h };
        JumpPackSpec[] variants =
        [
            Pack with { Name = "x" }, Pack with { HeightMap = [new(0.1f, 4f), new(0.4f, 18f)] }, Pack with { PowerCost = [new(0.1f, 30f)] },
            Pack with { MaxPower = 99f }, Pack with { RechargeRate = 15f }, Pack with { JumpingGravity = 21f }, Pack with { HangGravity = 11f },
            Pack with { HangTime = 0.4f }, Pack with { FallingGravity = 31f }, Pack with { ForwardSpeed = 15f }, Pack with { MoveGain = 0.03f },
            Pack with { DriftGain = 0.02f }, Pack with { PoundEnabled = false }, Pack with { PoundDelay = 2f }, Pack with { PoundGravity = 70f },
            Pack with { PoundSpeed = 40f }, Pack with { PoundLimitAngle = 45f }, Pack with { Pound = null }, Pack with { Pound = PoundBlast with { Radius = 6f } },
            Pack with { PoundReactions = [new(0.81f, "reaction_mp_stun")] }, Pack with { Landings = [] }, Pack with { TakeOffDelay = 0.25f },
            Pack with { LandMoveLock = 0.3f }, Pack with { LandJumpLock = 0.1f }, Pack with { LandIdleMoveLock = 0.5f }, Pack with { LandIdleJumpLock = 0.3f },
            Pack with { PoundRecovery = 0.4f },
        ];
        foreach (var v in variants) Assert.True(seen.Add((Config with { JumpPacks = [v] }).Hash()), $"{v} left the hash unchanged");
        Assert.True(seen.Add((Config with { Classes = [Config.Classes[0] with { JumpPack = -1 }, Config.Classes[1]] }).Hash()));
        Assert.True(seen.Add((Config with { JumpPacks = [] }).Hash()));
        Assert.Equal(new SimConfig().Hash(), new SimConfig { JumpPacks = [] }.Hash());
    }

    [Fact]
    public void A_pound_started_during_the_match_begins_countdown_does_nothing_and_the_same_inputs_dive_without_one()
    {
        int Dives(MatchRules rules, bool countdownOnly)
        {
            var w = new World(Config, null, [new SpawnPoint(new Vector3(0, 0, 0), 0f), new SpawnPoint(new Vector3(0, 3, 0), 0f)]) { Rules = rules };
            w.Spawn(1, Assault); w.Spawn(2, Tactical);   // classes without a side: the first joins the Space Marines, the second the smaller side
            int dives = 0;
            for (int k = 0; k < 90; k++)
            {
                if (countdownOnly && w.State == MatchState.Playing) break;
                // Rise holding Jump, then press melee in the air pitched down: the ground pound.
                var input = k < 30 ? new PlayerInput(0, 0, 0, 0f, InputButtons.Jump, 0f) : new PlayerInput(0, 0, 0, 0f, k % 2 == 0 ? InputButtons.Melee : InputButtons.None, -1.2f);
                w.Step(id => id == 1 ? input : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
                if (w.Players[1].Jump == JumpPhase.Dive) dives++;
                if (countdownOnly) { Assert.Empty(w.LastHits); Assert.Equal(w.Config.HealthMax, w.Players[2].Health); }
            }
            return dives;
        }
        Assert.True(Dives(new MatchRules { TimeLimit = 0f }, countdownOnly: false) > 0, "the control never dived");
        Assert.Equal(0, Dives(new MatchRules { TimeLimit = 0f, CountdownTime = 3f }, countdownOnly: true));
    }
}
