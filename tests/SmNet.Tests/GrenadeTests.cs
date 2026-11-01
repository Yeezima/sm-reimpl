using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Thrown equipment (M13): the throw's press edge, release and end timing, count, cooldown and refill, what a throw cancels
/// and what blocks it, the rigid-body grenade (bounce, ground fuse, wall and player contacts), blast fall-off steps, the
/// lingering melta-bomb blast, exact prediction under latency, the protocol 8 fields and the config hash. Synthetic items
/// shaped like the install's (values typed in; no game data).
/// </summary>
public class GrenadeTests(ITestOutputHelper output)
{
    internal static readonly RigidSpec Rigid = new(65f, 0.3f, 0.4f, 0.05f, 0.05f, [new(0f, 1f), new(90f, 0.2f)], [new(0f, 1f), new(90f, 0.2f)], 0.1f, 0.1f);
    internal static readonly DistancePoint[] FragFallOff = [new(0f, 1f), new(2.5f, 1f), new(2.6f, 0.5f), new(5f, 0.5f), new(5.1f, 0.3f), new(7f, 0.3f), new(10f, 0.05f)];
    /// <summary>Frag-like: 47.5 m/s, no gravity for the first 10 m, 1 bounce then stop, 1 s ground fuse, 30 s lifetime, blast r 10: 200 in steps.</summary>
    internal static readonly ProjectileSpec FragBall = new("ebps\\test\\frag", 47.5f, 30f, "rigidbody", 0f, [new(5f, 0f), new(10f, 1f)], false, true, false, 1, 1f, 1.5f, 0, null,
        new SplashSpec(10f, new DamageSpec(200f, 0f, 1f, 1f, 1f, 1f, false), FragFallOff, true, false, "target_all")) { Rigid = Rigid, StopAtMaxBounce = true };
    internal static readonly ThrownSpec Frag = new("test_frag", 2, 1, 1f, 10f, FragBall);
    internal static readonly ThrownSpec Krak = new("test_krak", 2, 2, 1f, 10f, FragBall with
    {
        Blueprint = "ebps\\test\\krak", Lifetime = 7f, Splash = new SplashSpec(30f, new DamageSpec(10f, 0f, 1f, 1f, 1f, 1f, false), [new(8f, 1f), new(8.05f, 0.5f), new(30f, 0.05f)], true, false, "target_all"),
    });
    /// <summary>Melta-bomb-like: a lingering blast (8 + 20 shield every 0.2 s for 10 s, r 8) then a burst (10 + 65 shield, r 6).</summary>
    internal static readonly ThrownSpec Bomb = new("test_bomb", 1, -1, 0.5f, 10f, FragBall with
    {
        Blueprint = "ebps\\test\\bomb", Speed = 30f, Gravity = [new(0f, 0.75f), new(8f, 1.8f)],
        Splash = new SplashSpec(8f, new DamageSpec(8f, 20f, 1f, 1f, 1f, 1f, true), [], true, false, "target_all") { Lifetime = 10f, Period = 0.2f },
        ExtraSplashes = [new SplashSpec(6f, new DamageSpec(10f, 65f, 1f, 1f, 1f, 1f, false), [new(4f, 1f), new(5f, 0.5f), new(6f, 0.05f)], true, false, "target_all")],
    });

    static readonly WeaponSpec Gun = new("test_gun", WeaponSlot.Ranged, TriggerKind.FullAuto, 10f, 0.1f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly WeaponSpec Sidearm = new("test_sidearm", WeaponSlot.Pistol, TriggerKind.SemiAuto, 10f, 0.2f, 10, 40, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly MeleeStep Knife = new(1.0f, 0.25f, 0.4f, 0.6f, 0.5f, 0.7f, 40f, 20f, 3.5f);
    const int Tactical = 0, Unarmed = 1, Charger = 2;
    const int FragChoice = 0, KrakChoice = 1, BombChoice = 2;

    internal static readonly SimConfig Config = new()
    {
        Weapons = [Gun, Sidearm, ChargeTests.Las],
        Thrown = [Frag, Krak, Bomb],
        Classes =
        [
            new ClassLoadout("test - tactical", 0, 1, [Knife]) { EquipmentChoices = [0, 1, 2] },
            new ClassLoadout("test - none", 0, -1, []),
            new ClassLoadout("test - charger", 2, -1, []) { EquipmentChoices = [1] },
        ],
        HealthMax = 1000f, ShieldMax = 100f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };

    static float Dt => Config.TickSeconds;

    static PlayerState Fresh(int playerClass = Tactical, int equipment = -1, SimConfig? c = null) =>
        Combat.Respawned(Loadouts.Choose(c ?? Config, new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = playerClass }, -1, -1, -1, equipment), c ?? Config);

    static SelfEvents Step(ref PlayerState s, InputButtons buttons, SimConfig? c = null)
    {
        s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 0, buttons), c ?? Config, out var events);
        return events;
    }

    // ---------------------------------------------------------------- the throw

    [Fact]
    public void A_press_starts_a_throw_that_releases_at_0_2_s_and_ends_at_0_667_s()
    {
        var s = Fresh();
        Assert.Equal((0, 1), (s.Equipment, s.Grenades));   // the class default (first choice); the frag's spawn count is 1 of 2
        var e = Step(ref s, InputButtons.Grenade);
        Assert.True(s.Throwing);
        Assert.Equal((0f, false, 1), (s.ThrowTime, e.Threw, s.Grenades));
        var released = new List<int>();
        for (int k = 1; k <= 25; k++)
        {
            e = Step(ref s, InputButtons.Grenade);   // held: no new throw
            if (e.Threw) released.Add(k);
            if (k == 6) Assert.Equal((0, 1f), (s.Grenades, s.GrenadeCooldown));
            if (k == 19) Assert.True(s.Throwing);
            if (k == 20) Assert.Equal((false, 0f), (s.Throwing, s.ThrowTime));
        }
        Assert.Equal([6], released);   // 6 ticks = 0.2 s
        // Empty: a press does nothing.
        Step(ref s, InputButtons.None);
        Step(ref s, InputButtons.Grenade);
        Assert.False(s.Throwing);
    }

    [Theory]
    [InlineData(35, false)]   // 29 ticks after the release: the 1 s cooldown is not over
    [InlineData(36, true)]    // 30 ticks: over
    public void The_next_throw_waits_for_the_cooldown_from_the_release(int pressAt, bool throws)
    {
        var s = Fresh(equipment: KrakChoice);
        Assert.Equal((1, 2), (s.Equipment, s.Grenades));
        Step(ref s, InputButtons.Grenade);
        for (int k = 1; k < pressAt; k++) Step(ref s, InputButtons.None);
        Step(ref s, InputButtons.Grenade);
        Assert.Equal(throws, s.Throwing);
        Assert.Equal(1, s.Grenades);   // the second one is spent only at its release
    }

    [Fact]
    public void A_throw_cancels_a_reload_and_a_charge_and_blocks_the_weapon_melee_and_switch()
    {
        var s = Fresh();
        for (int k = 0; k < 5; k++) Step(ref s, InputButtons.Fire);
        Step(ref s, InputButtons.Reload);
        Assert.True(s.ReloadLeft > 0f);
        Step(ref s, InputButtons.Grenade);
        Assert.True(s.Throwing);
        Assert.Equal(0f, s.ReloadLeft);   // the reload is lost
        int clip = s.Clip;
        var slot = s.Slot;
        for (int k = 0; k < 10; k++)
        {
            var buttons = k % 2 == 0 ? InputButtons.Fire | InputButtons.Melee | InputButtons.Switch | InputButtons.Reload : InputButtons.Fire;
            Assert.False(Step(ref s, buttons).Fired);
        }
        Assert.Equal((clip, slot, 0, 0f), (s.Clip, s.Slot, s.MeleeStep, s.ReloadLeft));
        // Control: after the throw the weapon fires again.
        for (int k = 0; k < 15; k++) Step(ref s, InputButtons.None);
        Assert.False(s.Throwing);
        Assert.True(Step(ref s, InputButtons.Fire).Fired);

        // A charge is cancelled.
        var c = Fresh(Charger);
        Step(ref c, InputButtons.Fire);
        Step(ref c, InputButtons.Fire);
        Assert.True(c.Charging);
        Step(ref c, InputButtons.Fire | InputButtons.Grenade);
        Assert.True(c.Throwing);
        Assert.False(c.Charging);
    }

    [Fact]
    public void Melee_switching_and_a_pending_shot_block_a_throw_and_death_cancels_one_unspent()
    {
        var s = Fresh();
        Step(ref s, InputButtons.Melee);
        Step(ref s, InputButtons.Grenade);
        Assert.False(s.Throwing);   // attacking
        Assert.True(s.MeleeStep > 0);
        // The same tick: melee wins.
        var t = Fresh();
        Step(ref t, InputButtons.Melee | InputButtons.Grenade);
        Assert.Equal((false, 1), (t.Throwing, t.MeleeStep));

        var w = Fresh();
        Step(ref w, InputButtons.Switch);
        Assert.True(w.SwitchLeft > 0f);
        Step(ref w, InputButtons.Grenade);
        Assert.False(w.Throwing);   // switching

        var d = Fresh();
        Step(ref d, InputButtons.Grenade);
        for (int k = 0; k < 3; k++) Step(ref d, InputButtons.Grenade);
        d = Combat.Killed(d, Config);
        Assert.Equal((false, 0f, 1), (d.Throwing, d.ThrowTime, d.Grenades));
        for (int k = 0; k < 10; k++) Assert.False(Step(ref d, InputButtons.Grenade).Threw);
        // A respawn refills to the spawn count (here: after spending one).
        var r = Fresh(equipment: KrakChoice);
        Step(ref r, InputButtons.Grenade);
        for (int k = 0; k < 6; k++) Step(ref r, InputButtons.None);
        Assert.Equal(1, r.Grenades);
        r = Combat.Respawned(Combat.Killed(r, Config) with { GrenadeCooldown = 0.5f }, Config);
        Assert.Equal((2, 0f, 1), (r.Grenades, r.GrenadeCooldown, r.Equipment));

        // A class without equipment never throws.
        var n = Fresh(Unarmed);
        Assert.Equal((-1, 0), (n.Equipment, n.Grenades));
        Step(ref n, InputButtons.Grenade);
        Assert.False(n.Throwing);
    }

    [Fact]
    public void Equipment_choices_are_validated_against_the_class()
    {
        Assert.Equal(2, Fresh(equipment: BombChoice).Equipment);
        Assert.Equal(1, Fresh(equipment: BombChoice).Grenades);   // initial -1: the full count
        Assert.Equal(0, Fresh(equipment: 7).Equipment);            // out of range: the class default
        Assert.Equal(1, Fresh(Charger, equipment: 0).Equipment);   // the charger's only choice is item 1
        var foreign = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Charger, Equipment = 0 }, Config);
        Assert.Equal(1, foreign.Equipment);                         // an item the class cannot carry: its default
        Assert.Equal("test - tactical: primary test_gun, pistol test_sidearm, melee default, equipment test_krak", Loadouts.Describe(Config, Fresh(equipment: KrakChoice)));
    }

    // ---------------------------------------------------------------- in the world

    static World WorldWith(SimConfig config, CollisionWorld? level, int equipment, params Vector3[] others)
    {
        var spawns = new List<SpawnPoint> { new(Vector3.Zero, 0f) };
        spawns.AddRange(others.Select(p => new SpawnPoint(p, MathF.PI)));
        var world = new World(config, level, spawns);
        world.Spawn(1, Tactical, equipmentChoice: equipment);
        for (int i = 1; i < spawns.Count; i++) world.Spawn(i + 1, Unarmed);
        for (int i = 0; i < spawns.Count; i++) Assert.Equal(spawns[i].Position, world.Players[i + 1].Position);
        return world;
    }

    static Func<int, PlayerInput> Thrower(InputButtons buttons, float pitch) =>
        id => new PlayerInput(0, 0, 0, id == 1 ? 0f : MathF.PI, id == 1 ? buttons : InputButtons.None, id == 1 ? pitch : 0f);

    /// <summary>Throws once (press on the first tick) and runs until the projectile is gone; returns the per-tick projectile states.</summary>
    static List<ProjectileState> ThrowAndTrack(World world, float pitch, List<HitEvent>? hits = null, List<DetonationEvent>? blasts = null, int maxTicks = 400)
    {
        var track = new List<ProjectileState>();
        world.Step(Thrower(InputButtons.Grenade, pitch));
        for (int k = 0; k < maxTicks; k++)
        {
            world.Step(Thrower(InputButtons.None, pitch));
            hits?.AddRange(world.LastHits);
            blasts?.AddRange(world.LastDetonations);
            if (world.Projectiles.Count > 0) track.Add(world.Projectiles[0]);
            else if (track.Count > 0 && world.Areas.Count == 0) break;
        }
        return track;
    }

    [Fact]
    public void A_frag_leaves_at_the_release_bounces_once_off_the_ground_and_goes_off_one_second_after_landing()
    {
        var world = WorldWith(Config, null, FragChoice, new Vector3(0, -40, 0));
        var blasts = new List<DetonationEvent>();
        world.Step(Thrower(InputButtons.Grenade, -0.5f));
        uint press = world.Tick;
        ThrowEvent? thrown = null;
        var track = new List<(uint Tick, ProjectileState P)>();
        for (int k = 0; k < 200 && blasts.Count == 0; k++)
        {
            world.Step(Thrower(InputButtons.None, -0.5f));
            if (world.LastThrows.Count > 0) thrown = world.LastThrows[0];
            blasts.AddRange(world.LastDetonations);
            if (world.Projectiles.Count > 0) track.Add((world.Tick, world.Projectiles[0]));
        }
        Assert.Equal(press + 6, thrown!.Value.Tick);
        Assert.Equal((1, 0), (thrown.Value.Thrower, thrown.Value.Item));
        Assert.Equal(0, world.Players[1].Grenades);
        var first = track.First(t => t.P.Landed);
        Assert.True(first.P.Thrown);
        Assert.Equal(1, first.P.Bounces);
        // No gravity before the contact (under 10 m flown), so the reflected velocity is the launch velocity with the ground
        // "reductions" kept: horizontal × 0.3, vertical × −0.4 (interpretation).
        float aim = -0.5f + 1.5f * MathF.PI / 180f;
        Assert.Equal(0f, first.P.Velocity.X, 3);
        Assert.Equal(47.5f * MathF.Cos(aim) * 0.3f, first.P.Velocity.Y, 2);
        Assert.Equal(-47.5f * MathF.Sin(aim) * 0.4f, first.P.Velocity.Z, 2);
        Assert.Equal(1.6f / MathF.Tan(-aim), first.P.Position.Y, 1);   // first contact where the straight throw meets the ground
        var rest = track.Last();
        Assert.Equal((ProjectileMotion.Stuck, 1), (rest.P.Motion, rest.P.Bounces));   // the second contact stops it
        var blast = Assert.Single(blasts);
        Assert.Equal(first.Tick + 30, blast.Tick);   // the ground fuse: 1 s from the first ground contact
        Assert.True(blast.Thrown);
        Assert.Equal(0, blast.Victims);              // the bystander 40 m behind is out of reach
        output.WriteLine($"landed tick {first.Tick} at y {first.P.Position.Y:F2}; rest at y {rest.P.Position.Y:F2}; blast tick {blast.Tick}");
    }

    [Fact]
    public void Blast_damage_steps_with_the_fall_off_points()
    {
        // Where the throw comes to rest (deterministic), then bystanders beside its path at chosen lateral distances.
        var probe = WorldWith(Config, null, FragChoice);
        var rest = ThrowAndTrack(probe, -0.5f).Last().Position;
        float[] lateral = [1.0f, -3.0f, 6.0f, -9.0f];
        var world = WorldWith(Config, null, FragChoice, lateral.Select(d => new Vector3(rest.X + d, rest.Y, 0f)).ToArray());
        var hits = new List<HitEvent>();
        var track = ThrowAndTrack(world, -0.5f, hits);
        Assert.Equal(rest, track.Last().Position);   // the bystanders were never in the way
        float chest = (Config.BodyBottom + Config.BodyTop) / 2f;
        float[] expectedIntensity = [1f, 0.5f, 0.3f, float.NaN];
        for (int i = 0; i < lateral.Length; i++)
        {
            var h = Assert.Single(hits, x => x.Target == i + 2 && x.Splash);
            float distance = MathF.Sqrt(lateral[i] * lateral[i] + MathF.Pow(chest - rest.Z, 2));
            Assert.Equal(distance, h.Distance, 3);
            float intensity = float.IsNaN(expectedIntensity[i]) ? 0.3f + (0.05f - 0.3f) * ((distance - 7f) / 3f) : expectedIntensity[i];
            Assert.Equal(intensity, Combat.SplashIntensity(FragFallOff, distance), 4);
            Assert.Equal(200f * intensity, h.ShieldDamage + h.HealthDamage, 2);
            output.WriteLine($"bystander at {lateral[i]} m: distance {distance:F2}, x{intensity:F3}, {h.ShieldDamage:F1} + {h.HealthDamage:F1}");
        }
        // Steps between points are linear (the M12 rule): just past 2.5 m the intensity is already dropping toward 0.5.
        Assert.Equal(0.75f, Combat.SplashIntensity(FragFallOff, 2.55f), 4);
        Assert.Equal(0f, Combat.SplashIntensity(FragFallOff, 10.01f));
    }

    [Fact]
    public void A_grenade_thrown_head_on_at_a_wall_keeps_a_fifth_of_its_speed_and_comes_to_rest_on_the_ground()
    {
        var level = Level(wallY: 6f);
        var world = WorldWith(Config, level, FragChoice);
        var blasts = new List<DetonationEvent>();
        var track = ThrowAndTrack(world, 0f, blasts: blasts);
        var hitWall = track.First(p => p.Bounces == 1);
        Assert.False(hitWall.Landed);
        // Nearly head-on (88.5° from the wall's surface): the wall map keeps 1 − 0.8 × 88.5 / 90 of each part of the reflection.
        float keep = 1f - 0.8f * 88.5f / 90f;
        Assert.Equal(-47.5f * MathF.Cos(1.5f * MathF.PI / 180f) * keep, hitWall.Velocity.Y, 2);
        var rest = track.Last();
        Assert.Equal((ProjectileMotion.Stuck, true, 1), (rest.Motion, rest.Landed, rest.Bounces));
        Assert.InRange(rest.Position.Y, 0.5f, hitWall.Position.Y - 1f);   // it came back off the wall before resting
        Assert.True(hitWall.Position.Y > 5.9f);
        Assert.InRange(rest.Position.Z, 0f, 0.05f);
        Assert.Single(blasts);
        Assert.Equal(rest.Position, blasts[0].Position);
    }

    [Fact]
    public void A_grenade_glances_off_a_player_without_damage_or_a_bounce()
    {
        var world = WorldWith(Config, null, FragChoice, new Vector3(0, 4f, 0));
        var hits = new List<HitEvent>();
        world.Step(Thrower(InputButtons.Grenade, 0f));
        ProjectileState? struck = null;
        for (int k = 0; k < 20 && struck is null; k++)
        {
            world.Step(Thrower(InputButtons.None, 0f));
            hits.AddRange(world.LastHits);
            if (world.Projectiles.Count > 0 && world.Projectiles[0].Struck == 2) struck = world.Projectiles[0];
        }
        Assert.NotNull(struck);
        Assert.Equal(0, struck!.Value.Bounces);
        Assert.InRange(struck.Value.Velocity.Y, -47.5f * 0.05f - 0.1f, -47.5f * 0.05f + 0.1f);   // reflected, 5 % kept: it drops at the player's feet
        Assert.Empty(hits);
    }

    [Fact]
    public void The_melta_bomb_applies_its_lingering_blast_every_0_2_s_for_10_s_and_its_burst_once()
    {
        var probe = WorldWith(Config, null, BombChoice);
        var rest = ThrowAndTrack(probe, -0.6f).Last().Position;
        var world = WorldWith(Config with { HealthMax = 1e6f }, null, BombChoice, new Vector3(rest.X + 2f, rest.Y, 0f));
        var hits = new List<HitEvent>();
        var blasts = new List<DetonationEvent>();
        ThrowAndTrack(world, -0.6f, hits, blasts, maxTicks: 800);
        var blast = Assert.Single(blasts);
        var mine = hits.Where(h => h.Target == 2 && h.Splash && h.Projectile == blast.Projectile).ToList();
        Assert.Equal(50 + 1, mine.Count);   // t = 0, 0.2, …, 9.8 s, plus the burst at t = 0
        Assert.Empty(world.Areas);
        Assert.Equal(2, mine.Count(h => h.Tick == blast.Tick));
        var ticks = mine.Select(h => h.Tick).Distinct().ToList();
        Assert.All(ticks.Zip(ticks.Skip(1)), p => Assert.Equal(6u, p.Second - p.First));
        output.WriteLine($"bomb at {rest}, {mine.Count} hits, {mine.Sum(h => h.ShieldDamage + h.HealthDamage):F0} damage");
    }

    [Fact]
    public void A_grenade_stopped_in_a_steep_crevice_starts_its_fuse_at_the_stop()
    {
        // A V-shaped crevice along +Y whose sides (69° from horizontal) are walls for a 65° ground angle; a floor patch for the thrower.
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        Quad(new(-3, -3, 0), new(3, -3, 0), new(3, 2, 0), new(-3, 2, 0));
        Quad(new(0, 2, -0.01f), new(0, 40, -0.01f), new(-1.5f, 40, 4), new(-1.5f, 2, 4));
        Quad(new(0, 2, -0.01f), new(1.5f, 2, 4), new(1.5f, 40, 4), new(0, 40, -0.01f));
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        var world = new World(Config, new CollisionWorld(mesh, mesh), [new SpawnPoint(Vector3.Zero, 0f)]);
        world.Spawn(1, Tactical);
        var blasts = new List<DetonationEvent>();
        var track = new List<(uint Tick, ProjectileState P)>();
        PlayerInput Aim(InputButtons b) => new(0, 0, 0, 0.05f, b, -0.1f);
        world.Step(_ => Aim(InputButtons.Grenade));
        for (int k = 0; k < 300 && blasts.Count == 0; k++)
        {
            world.Step(_ => Aim(InputButtons.None));
            blasts.AddRange(world.LastDetonations);
            if (world.Projectiles.Count > 0) track.Add((world.Tick, world.Projectiles[0]));
        }
        Assert.All(track, t => Assert.True(t.P.Bounces <= 1));
        Assert.False(track.First(t => t.P.Bounces == 1).P.Landed);   // the first contact was a wall, not ground
        var stop = track.First(t => t.P.Landed);
        Assert.Equal(1, stop.P.Bounces);   // the fuse started at the stop after its one bounce, both on walls
        var blast = Assert.Single(blasts);
        Assert.Equal(stop.Tick + 30, blast.Tick);   // not at the 30 s lifetime
        output.WriteLine($"stopped at tick {stop.Tick} at {stop.P.Position}; blast tick {blast.Tick} at {blast.Position}");
    }

    [Fact]
    public void A_thrower_is_hit_by_its_own_blast()
    {
        var world = WorldWith(Config, null, FragChoice);
        var hits = new List<HitEvent>();
        ThrowAndTrack(world, -1.3f, hits);   // steeply at its own feet
        var own = Assert.Single(hits, h => h.Target == 1);
        Assert.True(own.Splash && own.Shooter == 1 && own.ShieldDamage + own.HealthDamage > 0f);
    }

    [Fact]
    public void A_player_killed_mid_throw_by_a_shot_spends_nothing_and_throws_nothing()
    {
        var config = Config with { HealthMax = 15f, ShieldMax = 0f };
        var world = new World(config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 5, 0), MathF.PI)]);
        world.Spawn(1, Tactical);
        world.Spawn(2, Unarmed);
        var throws = new List<ThrowEvent>();
        PlayerState? dead = null;
        for (int k = 0; k < 20; k++)
        {
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, k == 0 ? InputButtons.Grenade : InputButtons.None)
                : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.Fire, MathF.Atan2(0.85f - 1.6f, 5f)));
            throws.AddRange(world.LastThrows);
            if (dead is null && !world.Players[1].Alive) { dead = world.Players[1]; Assert.True(k < 6, $"died at {k}, after the release"); }
        }
        Assert.NotNull(dead);
        Assert.Equal((false, 1), (dead!.Value.Throwing, dead.Value.Grenades));
        Assert.Empty(throws);
        Assert.Empty(world.Projectiles);
    }

    [Fact]
    public void Lingering_blasts_go_when_their_owner_leaves()
    {
        var world = WorldWith(Config, null, BombChoice);
        world.Step(Thrower(InputButtons.Grenade, -0.6f));
        for (int k = 0; k < 200 && world.Areas.Count == 0; k++) world.Step(Thrower(InputButtons.None, -0.6f));
        Assert.Single(world.Areas);
        world.Step(Thrower(InputButtons.None, -0.6f));
        Assert.Single(world.Areas);
        world.Remove(1);
        Assert.Empty(world.Areas);
    }

    static CollisionWorld Level(float wallY)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        Quad(new(-60, -60, 0), new(60, -60, 0), new(60, 60, 0), new(-60, 60, 0));
        Quad(new(-60, wallY, -1), new(60, wallY, -1), new(60, wallY, 20), new(-60, wallY, 20));
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(81)]
    [InlineData(82)]
    public void Prediction_matches_the_server_while_throwing_under_150ms_rtt_and_5pct_loss(int seed)
    {
        static ThrownSpec Harmless(ThrownSpec t) => t with
        {
            Count = 60, InitialCount = 60,
            Projectile = t.Projectile with
            {
                Splash = t.Projectile.Splash! with { Damage = t.Projectile.Splash.Damage with { Damage = 0f, ShieldDamage = 0f } },
                ExtraSplashes = t.Projectile.ExtraSplashes.Select(s => s with { Damage = s.Damage with { Damage = 0f, ShieldDamage = 0f } }).ToArray(),
            },
        };
        var config = Config with
        {
            Thrown = Config.Thrown.Select(Harmless).ToArray(),
            Weapons = [Gun with { Damage = 0f, ReserveSize = 5000 }, Sidearm with { Damage = 0f, ReserveSize = 5000 }, ChargeTests.Las],
            Classes = Config.Classes.Select(c => c with { Melee = c.Melee.Select(m => m with { Damage = 0f, SecondaryDamage = 0f }).ToArray() }).ToArray(),
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        for (int i = 0; i < 3; i++) h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = Tactical, RequestedEquipment = (byte)i });
        int n = h.Clients.Count;
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, n).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons & ~InputButtons.Jump;
            uint phase = (t + (uint)i * 7) % 120;
            if (phase is < 3 or (>= 40 and < 42) or (>= 50 and < 52)) buttons |= InputButtons.Grenade;   // the second press is in the cooldown
            if (phase is >= 20 and < 30 or (>= 44 and < 47)) buttons |= InputButtons.Fire;             // during and after a throw
            if (phase is 60 or 61) buttons |= InputButtons.Reload;
            if (phase is 62 or 63) buttons |= InputButtons.Grenade;                                     // cancels that reload
            if (phase is 90 or 91) buttons |= InputButtons.Melee;
            if (phase is 92 or 93) buttons |= InputButtons.Grenade;                                     // blocked by the attack
            if (phase is 105 or 106) buttons |= InputButtons.Switch;
            return move with { Buttons = buttons, Pitch = -0.3f };
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
            int throws = mine.Zip(mine.Skip(1)).Count(p => p.Second.Grenades == p.First.Grenades - 1);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches; {throws} throws; equipment {mine[^1].Equipment}");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.Equal(i, mine[^1].Equipment);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.True(throws >= 10, $"client {i}: only {throws} throws");
            Assert.Empty(mismatches);
        }
        Assert.Contains(h.Server.World.Players.Values, p => p.Grenades < 60);
    }

    // ---------------------------------------------------------------- protocol and hash

    static PlayerState Loaded(int id) => new(id, new Vector3(1e4f), new Vector3(100f), 3f, true)
    {
        Alive = true, Health = 1e3f, Shield = 1e3f, Class = Protocol.MaxClasses - 1, Slot = WeaponSlot.Pistol, Clip = 65535, Reserve = 65535, OtherClip = 65535, OtherReserve = 65535,
        Cooldown = 1, ReloadLeft = 1, SinceDamage = 1, RespawnLeft = 1, SwitchLeft = 1, Pitch = 1, MeleeStep = Protocol.MaxMeleeStep, MeleeTime = 1,
        MeleeQueued = true, MeleeHeld = true, FireHeld = true, SwitchHeld = true,
        Primary = 254, Pistol = 253, MeleeChoice = 254, Heat = 100f, Overheated = true, SinceHeat = 1f, OtherHeat = 99f, OtherOverheated = true, OtherSinceHeat = 2f,
        ReloadHeld = true, DetonateHeld = true, Charging = true, ChargeTime = 2.5f, PendingShot = 255, PendingLeft = 0.1f, ProjectilesOut = 255,
        Equipment = 254, Grenades = 255, GrenadeCooldown = 0.75f, Throwing = true, ThrowTime = 0.4f, GrenadeHeld = true,
    };

    [Fact]
    public void Protocol_8_carries_the_throw_state_within_1200_bytes_and_refuses_unknown_flags()
    {
        Assert.Equal(53, Protocol.Version);
        var players = Enumerable.Range(1, Protocol.MaxPlayers).Select(Loaded).ToArray();
        var projectiles = Enumerable.Range(0, Protocol.MaxSnapshotProjectiles)
            .Select(k => new ProjectileView(60000 + k, 254, k % 2 == 0 ? 0 : 255, new Vector3(k, -k, 3), k % 2 == 0 ? ProjectileFlags.Thrown | ProjectileFlags.Stuck : ProjectileFlags.Detonated)).ToArray();
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, players, Self: 7, Projectiles: projectiles, Match: new MatchView(MatchState.Playing, Team.None, uint.MaxValue, 65535, 65535), Kills: [.. Enumerable.Range(0, Protocol.MaxSnapshotKills).Select(k => new KillView(65535 - k, 65535, 1, KillCause.Thrown, 254))],
            Points: [.. Enumerable.Repeat(new ControlPointView(Team.Chaos, Team.Chaos, 1f, true), Protocol.MaxSnapshotPoints)], Shots: ShotTests.ManyShots(players, Protocol.MaxSnapshotShots)));
        output.WriteLine($"16 players and {projectiles.Length} projectiles (protocol {Protocol.Version}): {bytes.Length} bytes");
        Assert.Equal(Protocol.MaxSnapshotBytes, bytes.Length);   // protocol 16: 16 projectile records, the other blocks and the 5 shot records that fit
        Assert.True(bytes.Length <= UdpServerTransport.MaxPacket);
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal(players.Select(p => p.PlayerId == 7 ? p : Protocol.Slim(p)), back.Players);
        Assert.True(back.Players[0].Throwing);                       // slim records carry the throwing flag
        Assert.Equal((0, -1, 0f), (back.Players[0].Grenades, back.Players[0].Equipment, back.Players[0].ThrowTime));
        Assert.Equal(projectiles, back.Projectiles!);
        Assert.True(back.Projectiles![0].Thrown);
        for (int n = 0; n < bytes.Length; n++) Assert.Null(Protocol.Read(bytes.AsSpan(0, n)));

        // The own record: the grenade flags byte sits before the charge and melee flag bytes.
        var me = new PlayerState(3, Vector3.One, Vector3.Zero, 0f, true) { Alive = true, GrenadeHeld = true, Grenades = 2, Equipment = 1, GrenadeCooldown = 0.5f };
        var own = Protocol.Write(new Snapshot(1, 1, 1, [me], Self: 3));
        Assert.Equal(me, Assert.IsType<Snapshot>(Protocol.Read(own)).Players[0]);
        Assert.Equal(1, own[^35]);   // (protocol 11: the match block, kill count and point count, 11 bytes, follow the records; protocol 12: the 12-byte hit block; protocol 21: ready ticks in 2 bytes)
        own[^35] |= 0x38;
        Assert.Equal(BufferedAction.JumpLong, Assert.IsType<Snapshot>(Protocol.Read(own)).Players[0].Buffered);   // bits 3..5 the input buffer's action since 33 (2 and 4 are the slot buttons' held flags since protocol 14); 7, once unknown, is the long Jump press since 38
        own[^35] = 1 | 64;
        Assert.Null(Protocol.Read(own));   // buffer ticks (bits 6..7) without a buffered action (protocol 33)
        // Slim flags: 32 and 64 together (jumping and pounding), a thrown record with a stage, an unknown projectile flag (8).
        // (Bit 128 is the aiming flag since protocol 12.)
        var slim = Protocol.Write(new Snapshot(1, 1, 1, [me with { PlayerId = 4 }]));
        slim[^35] |= 96;   // flags byte of the slim record: … flags, class, in hand, melee choice, slot, step, time 4, evade 1 (protocol 13), then the match, kill, point and hit blocks

        Assert.Null(Protocol.Read(slim));
        byte[] One(ProjectileView q) => Protocol.Write(new Snapshot(1, 1, 1, [], Projectiles: [q]));
        Assert.NotNull(Protocol.Read(One(new ProjectileView(5, 1, 0, Vector3.One, ProjectileFlags.Thrown))));   // control
        Assert.Null(Protocol.Read(One(new ProjectileView(5, 1, 1, Vector3.One, ProjectileFlags.Thrown))));
        var flag = One(new ProjectileView(5, 1, 0, Vector3.One, ProjectileFlags.None));
        flag[13 + 1 + 4] |= 8;
        Assert.Null(Protocol.Read(flag));
    }

    [Fact]
    public void The_handshake_carries_the_requested_equipment_and_the_server_validates_it()
    {
        var hello = new Hello(Protocol.Version, 1, 2, 3, 4, 5, 6, 2, 1, 0, 7, 1);
        Assert.Equal(hello, Protocol.Read(Protocol.Write(hello)));
        Assert.Equal(Protocol.DefaultChoice, new Hello(Protocol.Version, 1, 2, 3).RequestedEquipment);
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(5), TimeSpan.Zero, 0), 3, 0, Config);
        h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = Tactical, RequestedEquipment = BombChoice });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = Tactical, RequestedEquipment = 9 });
        h.AddClient(nonce: 102, options: new ClientOptions { RequestedClass = Charger });
        h.Run(TimeSpan.FromSeconds(1));
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        int Equipment(int i) => h.Server.World.Players[h.Clients[i].PlayerId].Equipment;
        Assert.Equal((2, 0, 1), (Equipment(0), Equipment(1), Equipment(2)));
        Assert.Equal(2, h.Clients[0].Predicted.Equipment);   // the own record carries it
    }

    [Fact]
    public void The_client_ignores_thrown_projectiles_its_config_cannot_have()
    {
        var transport = new Scripted();
        var clock = new ManualClock();
        var client = new GameClient(Config, Harness.Fingerprint, transport, clock, nonce: 7);
        transport.Incoming.Enqueue(Protocol.Write(new Welcome(1, 100, 30, 7, 0, 5)));
        client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None));
        var other = new PlayerState(4, new Vector3(1, 2, 0), Vector3.Zero, 0, true) { Alive = true };
        transport.Incoming.Enqueue(Protocol.Write(new Snapshot(101, 0, 2, [other], Projectiles: [new ProjectileView(1, 3, 0, Vector3.One, ProjectileFlags.Thrown)])));   // no item 3
        client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None));
        Assert.Empty(client.Projectiles());
        transport.Incoming.Enqueue(Protocol.Write(new Snapshot(102, 0, 2, [other], Projectiles: [new ProjectileView(2, 2, 0, Vector3.One, ProjectileFlags.Thrown)])));   // control
        client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None));
        Assert.True(Assert.Single(client.Projectiles()).Thrown);
        var views = GameServer.ProjectilesFor([new ProjectileState(9, 1, 2, 0, Vector3.One, Vector3.Zero) { Thrown = true }],
            [new DetonationEvent(1, 10, 1, 0, 0, Vector3.Zero, 10f, 0, 0, 0, 0) { Thrown = true }], Vector3.Zero);
        Assert.All(views, v => Assert.True(v.Thrown));
    }

    [Fact]
    public void The_config_hash_covers_thrown_items_equipment_choices_and_throw_timing()
    {
        ulong h = Config.Hash();
        Assert.Equal(h, (Config with { Thrown = Config.Thrown.Select(t => t with { }).ToList(), Classes = Config.Classes.Select(c => c with { EquipmentChoices = c.EquipmentChoices.ToList() }).ToList() }).Hash());
        var seen = new HashSet<ulong> { h };
        var p = FragBall;
        ThrownSpec[] fragVariants =
        [
            Frag with { Count = 3 }, Frag with { InitialCount = 2 }, Frag with { Cooldown = 0.9f }, Frag with { Name = "x" },
            Frag with { Projectile = p with { GroundLifetime = 1.1f } }, Frag with { Projectile = p with { StopAtMaxBounce = false } },
            Frag with { Projectile = p with { Rigid = null } }, Frag with { Projectile = p with { Rigid = Rigid with { GroundAngle = 60f } } },
            Frag with { Projectile = p with { Rigid = Rigid with { WallVerticalReduction = [new(0f, 1f)] } } },
            Frag with { Projectile = p with { Splash = p.Splash! with { Period = 0.1f } } },
            Frag with { Projectile = p with { ExtraSplashes = [p.Splash!] } },
        ];
        foreach (var v in fragVariants) Assert.True(seen.Add((Config with { Thrown = [v, Krak, Bomb] }).Hash()), $"{v} left the hash unchanged");
        var t = Config.Classes[Tactical];
        foreach (var v in new[] { t with { EquipmentChoices = [0, 1] }, t with { EquipmentChoices = [1, 0, 2] }, t with { EquipmentChoices = [] } })
            Assert.True(seen.Add((Config with { Classes = [v, Config.Classes[1], Config.Classes[2]] }).Hash()));
        Assert.True(seen.Add((Config with { ThrowRelease = 0.25f }).Hash()));
        Assert.True(seen.Add((Config with { ThrowDuration = 0.7f }).Hash()));
        // Defaults (no install) are unchanged by the new fields' presence: two default configs agree.
        Assert.Equal(new SimConfig().Hash(), new SimConfig { Thrown = [] }.Hash());
    }

    sealed class Scripted : IClientTransport
    {
        public Queue<byte[]> Incoming { get; } = new();
        public void Send(byte[] packet) { }
        public bool TryReceive(out byte[] packet) => Incoming.TryDequeue(out packet!);
    }
}
