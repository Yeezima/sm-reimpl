using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Projectiles (M12): flight under the gravity table, lifetime, collision with the level and with players, bounces,
/// sticking, splash fall-off and line of sight, self damage and kill credit, the launcher's active limit and remote
/// detonation by reload, exact prediction of the grenade count, snapshots (size bound, round trip, refusals) and the
/// client's view. Synthetic weapons and levels only (no game data).
/// </summary>
public class ProjectileTests(ITestOutputHelper output)
{
    static readonly SplashSpec GrenadeBlast = new(6f, new DamageSpec(75f, 0f, 1f, 1f, 0f, 0f, false),
        [new(0f, 1f), new(4f, 1f), new(5f, 0.5f), new(6f, 0.05f)], true, false, "target_all");
    /// <summary>Grenade-launcher-like grenade (slower for short test levels): sticky, 1 bounce, no lifetime, at most 3 active.</summary>
    static readonly ProjectileSpec Grenade = new("ebps\\test\\grenade", 30f, 0f, "raycast", 0f, [new(0f, 1f)], false, false, true, 1, 0f, 0f, 3, null, GrenadeBlast);
    static WeaponSpec Weapon(string name, ProjectileSpec projectile, float directDamage = 0f, int clip = 10) =>
        new(name, WeaponSlot.Ranged, TriggerKind.SemiAuto, directDamage, 0.2f, clip, 20, 0.6f, 150f, 150f, 150f, 1f, 1f, 1f, 0f, 0f)
        { Delivery = DeliveryKind.Projectile, Projectile = projectile };
    static readonly WeaponSpec Launcher = Weapon("test_launcher", Grenade, directDamage: 10f) with { DetonationDelays = [0f, 0.25f, 0.32f] };

    /// <summary>A dart: 30 m/s (1 m per tick), no gravity unless given, detonates on impact; the blast is the grenade's.</summary>
    static ProjectileSpec Dart(float lifetime = 0f, bool detonateAtEnd = true, IReadOnlyList<DistancePoint>? gravity = null, SplashSpec? splash = null) =>
        new("ebps\\test\\dart", 30f, lifetime, "raycast", 0f, gravity ?? [new(0f, 0f)], true, detonateAtEnd, false, 0, 0f, 0f, 0, null, splash ?? GrenadeBlast);

    static SimConfig ConfigWith(params WeaponSpec[] weapons) => new()
    {
        Weapons = weapons, Classes = [new ClassLoadout("test", 0, -1, [])], HealthMax = 1000f, ShieldMax = 100f,
    };

    static readonly Vector3 Eye = new(0, 0, 1.6f);

    /// <summary>A world with players 1 (at the origin, facing +Y) and others at the given points (facing −Y).</summary>
    static World WorldWith(SimConfig config, CollisionWorld? level, params Vector3[] others)
    {
        var spawns = new List<SpawnPoint> { new(Vector3.Zero, 0f) };
        spawns.AddRange(others.Select(p => new SpawnPoint(p, MathF.PI)));
        var world = new World(config, level, spawns);
        for (int i = 0; i < spawns.Count; i++) world.Spawn(i + 1);
        for (int i = 0; i < spawns.Count; i++) Assert.Equal(spawns[i].Position, world.Players[i + 1].Position);
        return world;
    }

    static Func<int, PlayerInput> Shooter(InputButtons buttons, float pitch = 0f) =>
        id => new PlayerInput(0, 0, 0, id == 1 ? 0f : MathF.PI, id == 1 ? buttons : InputButtons.None, id == 1 ? pitch : 0f);
    static PlayerInput Idle(int id) => new(0, 0, 0, id == 1 ? 0f : MathF.PI, InputButtons.None);

    /// <summary>A flat floor (z = 0, ±60 m) and vertical walls: planes x = const or y = const, each ±60 m wide, z from −1 to 20.</summary>
    static CollisionWorld Level(float? wallY = null, float? wallX = null)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        Quad(new(-60, -60, 0), new(60, -60, 0), new(60, 60, 0), new(-60, 60, 0));
        if (wallY is { } y) Quad(new(-60, y, -1), new(60, y, -1), new(60, y, 20), new(-60, y, 20));
        if (wallX is { } x) Quad(new(x, -60, -1), new(x, 60, -1), new(x, 60, 20), new(x, -60, 20));
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    // ---------------------------------------------------------------- flight

    [Fact]
    public void Flight_follows_gravity_scaled_by_the_table_for_the_distance_flown()
    {
        // Factor 1 throughout, aimed 0.5 rad up: semi-implicit Euler at 20 m/s² (the config's gravity).
        var config = ConfigWith(Weapon("dart", Dart(gravity: [new(0f, 1f)])));
        var world = WorldWith(config, null);
        float dt = config.TickSeconds;
        var p = Eye; var v = Combat.AimDirection(0f, 0.5f) * 30f;
        world.Step(Shooter(InputButtons.Fire, 0.5f));
        int compared = 0;
        for (int k = 1; world.Projectiles.Count > 0 && k < 200; k++)
        {
            v.Z -= config.Gravity * dt;
            p += v * dt;
            var q = Assert.Single(world.Projectiles);
            Assert.True(Vector3.Distance(p, q.Position) < 1e-3f, $"tick {k}: expected {p}, got {q.Position}");
            compared++;
            world.Step(Idle);
        }
        Assert.True(compared > 30, $"{compared}");
        var blast = Assert.Single(world.LastDetonations);
        Assert.InRange(blast.Position.Z, 0f, 0.06f);   // detonated on the flat ground

        // Step lookup: ×0 until 5.1 m flown, then ×1. Flying level at 1 m per tick, z stays for 6 ticks and drops in the 7th.
        var stepped = WorldWith(ConfigWith(Weapon("dart", Dart(gravity: [new(0f, 0f), new(5f, 0f), new(5.1f, 1f)]))), null);
        stepped.Step(Shooter(InputButtons.Fire));
        var heights = new List<float> { stepped.Projectiles[0].Position.Z };
        for (int k = 0; k < 7; k++) { stepped.Step(Idle); heights.Add(stepped.Projectiles[0].Position.Z); }
        Assert.All(heights.Take(6), z => Assert.Equal(1.6f, z));
        Assert.True(heights[6] < 1.6f);
        Assert.Equal([0f, 0f, 0f, 1f, 1f], new[] { 0f, 4.9f, 5.05f, 5.1f, 100f }.Select(d => Combat.GravityFactor([new(0f, 0f), new(5f, 0f), new(5.1f, 1f)], d)));
        Assert.Equal(0.25f, Combat.GravityFactor([new(2f, 0.25f)], 1f));   // before the first point: its value
        Assert.Equal(1f, Combat.GravityFactor([], 3f));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_lifetime_ends_the_projectile_detonating_only_when_the_file_says_so(bool detonateAtEnd)
    {
        var world = WorldWith(ConfigWith(Weapon("dart", Dart(lifetime: 0.5f, detonateAtEnd: detonateAtEnd))), null);
        world.Step(Shooter(InputButtons.Fire, 1.2f));   // launch tick: age 1/30
        uint launched = world.Tick;
        var detonations = new List<DetonationEvent>();
        uint gone = 0;
        for (int k = 0; k < 30; k++)
        {
            world.Step(Idle);
            detonations.AddRange(world.LastDetonations);
            if (gone == 0 && world.Projectiles.Count == 0) gone = world.Tick;
        }
        Assert.Equal(launched + 14, gone);   // age 15/30 = 0.5 s
        if (detonateAtEnd) Assert.Equal(launched + 14, Assert.Single(detonations).Tick);
        else Assert.Empty(detonations);
    }

    // ---------------------------------------------------------------- impacts

    [Fact]
    public void A_wall_stops_a_projectile_that_detonates_on_collision()
    {
        var world = WorldWith(ConfigWith(Weapon("dart", Dart())), Level(wallY: 10f));
        world.Step(Shooter(InputButtons.Fire));
        DetonationEvent? blast = null;
        for (int k = 0; k < 20 && blast is null; k++) { world.Step(Idle); if (world.LastDetonations.Count > 0) blast = world.LastDetonations[0]; }
        Assert.NotNull(blast);
        Assert.Equal(9.95f, blast!.Value.Position.Y, 3);   // 5 cm off the wall, on the shooter's side
        Assert.Equal(1.6f, blast.Value.Position.Z, 3);
        Assert.Empty(world.Projectiles);
    }

    [Fact]
    public void A_sticky_grenade_without_bounces_sticks_to_the_wall_it_strikes()
    {
        var world = WorldWith(ConfigWith(Weapon("launcher", Grenade with { MaxBounces = 0, Gravity = [new(0f, 0f)] })), Level(wallY: 5f));
        world.Step(Shooter(InputButtons.Fire));
        for (int k = 0; k < 30; k++) world.Step(Idle);
        var g = Assert.Single(world.Projectiles);
        Assert.Equal((ProjectileMotion.Stuck, 0), (g.Motion, g.Bounces));
        Assert.Equal(5f - 0.02f, g.Position.Y, 3);
        Assert.Equal(1.6f, g.Position.Z, 3);
        Assert.Empty(world.LastDetonations);
    }

    [Fact]
    public void A_sticky_grenade_never_bounces_even_when_its_data_allows_a_bounce_it_sticks_at_the_first_contact()
    {
        var world = WorldWith(ConfigWith(Weapon("launcher", Grenade)), Level(wallY: 10f));   // MaxBounces 1, sticky
        world.Step(Shooter(InputButtons.Fire, 0.1f));
        for (int k = 0; k < 90; k++) world.Step(Idle);
        var g = Assert.Single(world.Projectiles);
        Assert.Equal((ProjectileMotion.Stuck, 0), (g.Motion, g.Bounces));
        Assert.InRange(g.Position.Y, 9.9f, 10f);   // on the wall, 2 cm off the surface
        Assert.Equal(Vector3.Zero, g.Velocity);
        Assert.Empty(world.LastDetonations);
    }

    [Fact]
    public void A_sticky_grenade_sticks_to_the_floor_and_the_ceiling()
    {
        var down = WorldWith(ConfigWith(Weapon("launcher", Grenade)), Level());
        down.Step(Shooter(InputButtons.Fire, -0.2f));
        for (int k = 0; k < 60; k++) down.Step(Idle);
        var f = Assert.Single(down.Projectiles);
        Assert.Equal((ProjectileMotion.Stuck, 0), (f.Motion, f.Bounces));
        Assert.Equal(0.02f, f.Position.Z, 3);

        var v = new List<Vector3>(); var idx = new List<int>();
        v.AddRange([new(-60, -60, 4), new(60, -60, 4), new(60, 60, 4), new(-60, 60, 4)]); idx.AddRange([0, 2, 1, 0, 3, 2]);
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        var up = WorldWith(ConfigWith(Weapon("launcher", Grenade with { Gravity = [new(0f, 0f)] })), new CollisionWorld(mesh, mesh));
        up.Step(Shooter(InputButtons.Fire, 0.5f));
        for (int k = 0; k < 60; k++) up.Step(Idle);
        var c = Assert.Single(up.Projectiles);
        Assert.Equal((ProjectileMotion.Stuck, 0), (c.Motion, c.Bounces));
        Assert.Equal(4f - 0.02f, c.Position.Z, 2);
    }

    [Fact]
    public void A_sticky_shot_sticks_to_the_player_it_strikes_follows_them_and_detonates_at_its_lifetime()
    {
        var sticky = Grenade with { Lifetime = 1f, DetonateWhenLifetimeOver = true, Gravity = [new(0f, 0f)] };
        var world = WorldWith(ConfigWith(Weapon("sticky", sticky, directDamage: 10f)), null, new Vector3(0, 8, 0));
        world.Step(Shooter(InputButtons.Fire));
        var hits = new List<HitEvent>();
        for (int k = 0; k < 8; k++) { world.Step(Idle); hits.AddRange(world.LastHits); }
        var g = Assert.Single(world.Projectiles);
        Assert.Equal((ProjectileMotion.Attached, 2), (g.Motion, g.AttachedTo));
        var direct = Assert.Single(hits);
        Assert.Equal((1, 2, false, false), (direct.Shooter, direct.Target, direct.Splash, direct.Melee));
        Assert.Equal(g.Id, direct.Projectile);
        // The target walks; the grenade goes with it.
        var offset = g.Position - world.Players[2].Position;
        for (int k = 0; k < 10; k++) world.Step(id => id == 2 ? new PlayerInput(0, 1, 0, MathF.PI, InputButtons.None) : Idle(id));
        Assert.Equal(world.Players[2].Position + offset, world.Projectiles[0].Position);
        Assert.NotEqual(new Vector3(0, 8, 0), world.Players[2].Position);
        float before = world.Players[2].Health;
        DetonationEvent? blast = null;
        for (int k = 0; k < 30 && blast is null; k++) { world.Step(Idle); if (world.LastDetonations.Count > 0) blast = world.LastDetonations[0]; }
        Assert.NotNull(blast);
        Assert.Equal(world.Config.TickRate - 1, (int)(blast!.Value.Tick - g.LaunchTick));   // 1 s after launch
        Assert.True(world.Players[2].Health < before);
    }

    // ---------------------------------------------------------------- splash

    [Fact]
    public void Splash_intensity_interpolates_between_the_fall_off_points()
    {
        DistancePoint[] points = [new(0f, 1f), new(4f, 1f), new(5f, 0.5f), new(6f, 0.05f)];
        Assert.Equal(1f, Combat.SplashIntensity(points, 2f));
        Assert.Equal(0.75f, Combat.SplashIntensity(points, 4.5f), 5);
        Assert.Equal(0.275f, Combat.SplashIntensity(points, 5.5f), 5);
        Assert.Equal(0.05f, Combat.SplashIntensity(points, 6f), 5);
        Assert.Equal(0f, Combat.SplashIntensity(points, 6.01f));           // beyond the last point
        Assert.Equal(0.8f, Combat.SplashIntensity([new(2f, 0.8f), new(7f, 0.1f)], 1f));   // before the first: its value
        Assert.Equal(0.6f, Combat.SplashIntensity([new(2f, 1f), new(2f, 0.6f), new(3f, 0.6f)], 2.001f), 5);   // a step pair
        Assert.Equal(1f, Combat.SplashIntensity([], 3f));
    }

    /// <summary>A dart detonating in the air at (0, 6, 1.6) after 6 ticks (lifetime 0.2 s at 1 m per tick).</summary>
    static World Blast(CollisionWorld? level, SplashSpec? splash, params Vector3[] others)
    {
        var world = WorldWith(ConfigWith(Weapon("dart", Dart(lifetime: 0.2f, splash: splash))), level, others);
        world.Step(Shooter(InputButtons.Fire));
        for (int k = 0; k < 10 && world.LastDetonations.Count == 0; k++) world.Step(Idle);
        var blast = Assert.Single(world.LastDetonations);
        Assert.True(Vector3.Distance(new Vector3(0, 6, 1.6f), blast.Position) < 1e-3f, $"{blast.Position}");
        return world;
    }

    [Fact]
    public void Splash_damage_scales_with_the_fall_off_at_the_targets_distance()
    {
        var targets = new[] { new Vector3(3f, 6f, 0f), new Vector3(-4.5f, 6f, 0f), new Vector3(0f, 11.6f, 0f), new Vector3(-7f, 6f, 0f) };   // 3.1, 4.6, 5.65 and 7.04 m from the blast
        var world = Blast(null, null, targets);
        float chest = (world.Config.BodyBottom + world.Config.BodyTop) / 2f;
        for (int i = 0; i < targets.Length; i++)
        {
            float d = Vector3.Distance(new Vector3(0, 6, 1.6f), targets[i] + new Vector3(0, 0, chest));
            float intensity = d <= 4f ? 1f : d <= 5f ? 1f - 0.5f * (d - 4f) : d <= 6f ? 0.5f - 0.45f * (d - 5f) : 0f;
            output.WriteLine($"target {i + 2}: {d:F3} m, intensity {intensity:F4}, health {world.Players[i + 2].Health}");
            Assert.Equal(1000f - 75f * intensity, world.Players[i + 2].Health, 2);
            Assert.Equal(100f, world.Players[i + 2].Shield);   // the blast's shield multipliers are 0
        }
        Assert.Equal(1000f, world.Players[1].Health);          // the shooter, 6.05 m away, is outside the radius
        Assert.Equal(3, world.LastDetonations[0].Victims);
    }

    [Fact]
    public void A_blast_with_ignore_self_spares_its_owner_and_one_without_hits_it()
    {
        var wide = GrenadeBlast with { Radius = 10f, FallOff = [] };   // reaches the shooter 6.05 m away
        Assert.True(Blast(null, wide).Players[1].Health < 1000f);                          // ignore_self false (every MP projectile)
        Assert.Equal(1000f, Blast(null, wide with { IgnoreSelf = true }).Players[1].Health);
    }

    [Fact]
    public void A_wall_between_the_blast_and_a_target_blocks_the_splash_when_line_of_sight_is_required()
    {
        var target = new Vector3(3f, 6f, 0f);
        Assert.Equal(1000f, Blast(Level(wallX: 1.5f), null, target).Players[2].Health);           // blocked
        Assert.Equal(925f, Blast(Level(wallX: -1.5f), null, target).Players[2].Health, 2);        // control: the wall elsewhere
        Assert.Equal(925f, Blast(Level(wallX: 1.5f), GrenadeBlast with { LineOfSight = false }, target).Players[2].Health, 2);   // no line of sight needed
    }

    [Fact]
    public void A_blast_hurts_its_owner_and_kills_are_credited_to_the_owner()
    {
        // Detonating 2 m ahead: the owner (2.14 m from the blast) takes the full 75.
        var config = ConfigWith(Weapon("dart", Dart(lifetime: 2f / 30f)));
        var world = WorldWith(config, null, new Vector3(1f, 2f, 0f));
        world.Step(Shooter(InputButtons.Fire));
        var hits = new List<HitEvent>(world.LastHits);
        for (int k = 0; k < 3; k++) { world.Step(Idle); hits.AddRange(world.LastHits); }
        Assert.Equal(925f, world.Players[1].Health, 2);
        Assert.Contains(hits, h => h.Shooter == 1 && h.Target == 1 && h.Splash);

        // A weak target dies: the owner is credited.
        var weak = config with { HealthMax = 50f, ShieldMax = 0f };
        world = WorldWith(weak, null, new Vector3(1f, 2f, 0f));
        world.Step(Shooter(InputButtons.Fire));
        hits = new List<HitEvent>(world.LastHits);
        var blasts = new List<DetonationEvent>(world.LastDetonations);
        for (int k = 0; k < 3; k++) { world.Step(Idle); hits.AddRange(world.LastHits); blasts.AddRange(world.LastDetonations); }
        Assert.Contains(hits, h => h.Shooter == 1 && h.Target == 2 && h.Killed);
        Assert.False(world.Players[2].Alive);
        Assert.Equal(2, Assert.Single(blasts).Kills);   // the shooter too
    }

    // ---------------------------------------------------------------- the grenade launcher

    static World LauncherWorld(out SimConfig config)
    {
        config = ConfigWith(Launcher);
        return WorldWith(config, Level());
    }

    /// <summary>Fires one grenade at the floor ahead (press, release, then waits for the cooldown).</summary>
    static void FireGrenade(World world)
    {
        world.Step(Shooter(InputButtons.Fire, -0.4f));
        for (int k = 0; k < 7; k++) world.Step(id => new PlayerInput(0, 0, 0, 0, InputButtons.None, id == 1 ? -0.4f : 0f));
    }

    [Fact]
    public void A_fourth_grenade_detonates_the_oldest()
    {
        var world = LauncherWorld(out _);
        for (int n = 1; n <= 3; n++)
        {
            FireGrenade(world);
            Assert.Equal(n, world.Projectiles.Count);
            Assert.Equal(n, world.Players[1].ProjectilesOut);
        }
        int oldest = world.Projectiles[0].Id;
        world.Step(Shooter(InputButtons.Fire, -0.4f));
        Assert.Equal(oldest, Assert.Single(world.LastDetonations).Projectile);
        Assert.Equal(3, world.Projectiles.Count);
        Assert.Equal(3, world.Players[1].ProjectilesOut);
        Assert.DoesNotContain(world.Projectiles, p => p.Id == oldest);
    }

    [Fact]
    public void Reload_with_grenades_out_detonates_them_after_the_delays_instead_of_reloading()
    {
        var world = LauncherWorld(out var config);
        for (int n = 0; n < 3; n++) FireGrenade(world);
        for (int k = 0; k < 30; k++) world.Step(Idle);
        Assert.All(world.Projectiles, p => Assert.Equal(ProjectileMotion.Stuck, p.Motion));
        var ids = world.Projectiles.Select(p => p.Id).ToList();
        Assert.Equal(7, world.Players[1].Clip);   // the clip is not full, so a reload could start

        world.Step(Shooter(InputButtons.Reload));
        uint pressed = world.Tick;
        Assert.Equal(0, world.Players[1].ProjectilesOut);
        Assert.Equal(0f, world.Players[1].ReloadLeft);
        var when = new Dictionary<int, uint>();
        foreach (var d in world.LastDetonations) when[d.Projectile] = d.Tick;
        for (int k = 0; k < 20; k++)
        {
            world.Step(Idle);
            foreach (var d in world.LastDetonations) when[d.Projectile] = d.Tick;
        }
        Assert.Equal(0f, world.Players[1].ReloadLeft);
        Assert.Equal(new uint[] { 0, 8, 10 }, ids.Select(id => when[id] - pressed));   // 0, 0.25 and 0.32 s, in ticks rounded up
        Assert.Empty(world.Projectiles);

        // Control: a press with nothing out reloads.
        world.Step(Idle);
        world.Step(Shooter(InputButtons.Reload));
        Assert.True(world.Players[1].ReloadLeft > 0f);
        _ = config;
    }

    // ---------------------------------------------------------------- network

    [Fact]
    public void Prediction_of_the_grenade_count_stays_exact_and_clients_see_projectiles_and_detonations()
    {
        var harmless = Launcher with { Damage = 0f, Projectile = Grenade with { Splash = GrenadeBlast with { Damage = GrenadeBlast.Damage with { Damage = 0f } } }, ReserveSize = 2000 };
        var config = ConfigWith(harmless);
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, 81, 2, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new Dictionary<uint, PlayerState>();
        int seenProjectiles = 0, maxOut = 0;
        var blasts = new List<ProjectileView>();
        PlayerInput Input(int i, uint t)
        {
            uint phase = t % 120;
            var b = InputButtons.None;
            if (i == 0 && phase % 12 == 0 && phase < 60) b |= InputButtons.Fire;   // five presses: the 4th and 5th detonate the oldest
            if (i == 0 && phase is 90 or 91) b |= InputButtons.Reload;          // detonate (or reload when none are out)
            return new PlayerInput(t, 0, 0, i * 3f, b, -0.5f);
        }
        h.Run(TimeSpan.FromSeconds(16), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            if (h.Clients[0].Status == ClientStatus.Connected) predicted[h.Clients[0].Tick] = h.Clients[0].Predicted;
            maxOut = Math.Max(maxOut, h.Clients[0].Predicted.ProjectilesOut);
            if (h.Clients[1].Status == ClientStatus.Connected)
            {
                seenProjectiles = Math.Max(seenProjectiles, h.Clients[1].Projectiles().Count);
                blasts.AddRange(h.Clients[1].TakeDetonations());
            }
        });
        int id = h.Clients[0].PlayerId, compared = 0;
        var mismatches = new List<string>();
        foreach (var (tick, pred) in predicted)
        {
            if (tick < truth.Keys.First() + 90 || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
            compared++;
            if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
        }
        output.WriteLine($"compared {compared}, {mismatches.Count} mismatches; at most {maxOut} grenades out; client 1 saw up to {seenProjectiles} projectiles and {blasts.Count} detonations");
        foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
        foreach (var m in mismatches.Take(1)) output.WriteLine(m);
        Assert.True(compared > 300);
        Assert.Equal(3, maxOut);
        Assert.True(seenProjectiles >= 3, $"{seenProjectiles}");
        Assert.True(blasts.Count >= 10, $"{blasts.Count}");
        Assert.Equal(blasts.Count, blasts.Select(b => b.Id).Distinct().Count());   // each once
        Assert.All(blasts, b => Assert.Equal((0, 0), (b.Weapon, b.Stage)));
        Assert.Empty(mismatches);
    }

    static ProjectileView[] ManyProjectiles(int n) => Enumerable.Range(0, n)
        .Select(k => new ProjectileView(60000 + k, 254, 255, new Vector3(-1e5f + k, 1e5f, -3.3f), k % 3 == 0 ? ProjectileFlags.Detonated : k % 3 == 1 ? ProjectileFlags.Stuck : ProjectileFlags.Attached))
        .ToArray();

    [Fact]
    public void A_sixteen_player_snapshot_with_the_most_projectiles_stays_within_1200_bytes_and_round_trips()
    {
        var players = Enumerable.Range(1, Protocol.MaxPlayers).Select(i => new PlayerState(i, new Vector3(1e4f), new Vector3(100f), 3f, true)
        {
            Alive = true, Health = 1e3f, Shield = 1e3f, Class = Protocol.MaxClasses - 1, Slot = WeaponSlot.Pistol, Clip = 65535, Reserve = 65535, OtherClip = 65535, OtherReserve = 65535,
            Cooldown = 1, ReloadLeft = 1, SinceDamage = 1, RespawnLeft = 1, SwitchLeft = 1, Pitch = 1, MeleeStep = Protocol.MaxMeleeStep, MeleeTime = 1,
            MeleeQueued = true, MeleeHeld = true, FireHeld = true, SwitchHeld = true,
            Primary = 254, Pistol = 253, MeleeChoice = 254, Heat = 100f, Overheated = true, SinceHeat = 1f, OtherHeat = 99f, OtherOverheated = true, OtherSinceHeat = 2f,
            ReloadHeld = true, DetonateHeld = true, Charging = true, ChargeTime = 2.5f, PendingShot = 255, PendingLeft = 0.1f, ProjectilesOut = 255,
        }).ToArray();
        var projectiles = ManyProjectiles(Protocol.MaxSnapshotProjectiles);
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, players, Self: 7, Projectiles: projectiles, Match: new MatchView(MatchState.Playing, Team.None, uint.MaxValue, 65535, 65535), Kills: [.. Enumerable.Range(0, Protocol.MaxSnapshotKills).Select(k => new KillView(65535 - k, 65535, 1, KillCause.Thrown, 254))],
            Points: [.. Enumerable.Repeat(new ControlPointView(Team.Chaos, Team.Chaos, 1f, true), Protocol.MaxSnapshotPoints)], Shots: ShotTests.ManyShots(players, Protocol.MaxSnapshotShots)));
        output.WriteLine($"16 players and {projectiles.Length} projectiles (protocol {Protocol.Version}): {bytes.Length} bytes");
        Assert.Equal(Protocol.MaxSnapshotBytes, bytes.Length);   // protocol 16: 16 projectile records (19 in 13-15, 20 in 10-12, 26 before), the other blocks and the shot records that fit
        Assert.Equal(1194, bytes.Length);   // protocol 45: 14 projectile records and the own record's 8 reload bytes (44: 1200); protocol 23: + the 2-byte perk word, exactly the budget (21, 22: 1198); protocol 21: 16 projectile records, 4 shot records, the own record's reaction and raise bytes (ready ticks in 2), 2 under the 1200-byte budget (20: 1197; 17-19: 1195; 16: 1199; 15: 1195; 13: 1191; 12: 1187; 11: 1175)
        Assert.True(bytes.Length <= UdpServerTransport.MaxPacket);
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal(players.Select(p => p.PlayerId == 7 ? p : Protocol.Slim(p)), back.Players);
        Assert.True(back.Players[0].Charging);   // slim records carry the charging flag
        Assert.Equal(0f, back.Players[0].ChargeTime);
        Assert.Equal(projectiles.Select(q => q with { Position = Protocol.Quantize(q.Position) }), back.Projectiles!);
        for (int n = 0; n < bytes.Length; n++) Assert.Null(Protocol.Read(bytes.AsSpan(0, n)));
        // More than the limit are not written.
        Assert.Equal(Protocol.MaxSnapshotProjectiles, Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [], Projectiles: ManyProjectiles(40))))).Projectiles!.Length);
        // Quantization: 1/64 m, clamped, NaN as 0.
        Assert.Equal(new Vector3(1.015625f, -2f, 0f), Protocol.Quantize(new Vector3(1.01f, -2.004f, float.NaN)));
        Assert.Equal(((1 << 23) - 1) / 64f, Protocol.Quantize(new Vector3(1e9f, 0, 0)).X);
    }

    [Fact]
    public void Bad_projectile_records_are_refused()
    {
        var good = new ProjectileView(5, 1, 0, new Vector3(1, 2, 3), ProjectileFlags.Stuck);
        byte[] Write(params ProjectileView[] q) => Protocol.Write(new Snapshot(1, 1, 1, [], Projectiles: q));
        Assert.NotNull(Protocol.Read(Write(good, good with { Id = 6 })));                // control
        Assert.Null(Protocol.Read(Write(good, good with { Position = Vector3.One })));    // the same id twice
        Assert.Null(Protocol.Read(Write(good with { Weapon = -1 })));                     // no weapon
        var flags = Write(good);
        Assert.Equal((byte)ProjectileFlags.Stuck, flags[13 + 1 + 4]);
        flags[13 + 1 + 4] |= 8;                                                           // an unknown flag
        Assert.Null(Protocol.Read(flags));
        // A count above the limit, with the records present.
        var full = Write(ManyProjectiles(Protocol.MaxSnapshotProjectiles));
        var one = Write(good with { Id = 7 });
        // The records end 25 bytes before the end (player count, match block, kill count, point count, hit block; protocol 12; shot count, 16).
        var over = full[..^25].Concat(one.AsSpan(14, 14).ToArray()).Concat(full[^25..]).ToArray();
        over[13] = (byte)(Protocol.MaxSnapshotProjectiles + 1);
        Assert.Null(Protocol.Read(over));
        var fits = Write(ManyProjectiles(Protocol.MaxSnapshotProjectiles - 1));          // control: the same splice at the limit
        var spliced = fits[..^25].Concat(one.AsSpan(14, 14).ToArray()).Concat(fits[^25..]).ToArray();
        spliced[13] = Protocol.MaxSnapshotProjectiles;
        Assert.Equal(Protocol.MaxSnapshotProjectiles, Assert.IsType<Snapshot>(Protocol.Read(spliced)).Projectiles!.Length);
        // An unknown charge flag in the own record (2 and 4 are Reload held and detonation held; 1, AltFire held until protocol 19, is refused since 20).
        var me = new PlayerState(3, Vector3.One, Vector3.Zero, 0f, true) { Alive = true, ReloadHeld = true, DetonateHeld = true };
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, [me], Self: 3));
        Assert.Equal(6, bytes[^34]);   // before the last flag byte, the reaction, lock and raise bytes (ready ticks 2 since protocol 21), the match block and the kill count
        Assert.Equal(me, Assert.IsType<Snapshot>(Protocol.Read(bytes)).Players[0]);
        foreach (int unknown in new[] { 1, 8 })
        {
            var bad = bytes.ToArray();
            bad[^34] |= (byte)unknown;
            Assert.Null(Protocol.Read(bad));
        }
    }

    [Fact]
    public void The_client_ignores_snapshots_with_projectiles_its_config_cannot_have()
    {
        var config = ConfigWith(Launcher, Weapon("ray", Dart()) with { Delivery = DeliveryKind.Raycast, Projectile = null, Damage = 5f });
        var transport = new ScriptedTransport();
        var clock = new ManualClock();
        var client = new GameClient(config, Harness.Fingerprint, transport, clock, nonce: 7);
        transport.Incoming.Enqueue(Protocol.Write(new Welcome(1, 100, 30, 7, 0, 5)));
        client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None));
        var other = new PlayerState(4, new Vector3(1, 2, 0), Vector3.Zero, 0, true) { Alive = true };
        transport.Incoming.Enqueue(Protocol.Write(new Snapshot(101, 0, 2, [other], Projectiles: [new ProjectileView(1, 2, 0, Vector3.One, ProjectileFlags.None)])));   // no weapon 2
        transport.Incoming.Enqueue(Protocol.Write(new Snapshot(102, 0, 2, [other], Projectiles: [new ProjectileView(2, 1, 0, Vector3.One, ProjectileFlags.None)])));   // a ray weapon
        transport.Incoming.Enqueue(Protocol.Write(new Snapshot(103, 0, 2, [other], Projectiles: [new ProjectileView(3, 0, 1, Vector3.One, ProjectileFlags.None)])));   // no charge stage
        clock.Advance(TimeSpan.FromMilliseconds(1));
        client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None));
        Assert.Empty(client.Projectiles());
        Assert.Empty(client.RemotePlayers().Players);
        transport.Incoming.Enqueue(Protocol.Write(new Snapshot(104, 0, 2, [other], Projectiles: [new ProjectileView(4, 0, 0, Vector3.One, ProjectileFlags.Stuck)])));   // control
        client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None));
        Assert.Equal(4, Assert.Single(client.Projectiles()).Id);
    }

    [Fact]
    public void Server_picks_detonations_first_then_the_nearest_projectiles()
    {
        var world = WorldWith(ConfigWith(Weapon("sticky", Grenade with { MaxActive = 0 }, clip: 100)) with { MaxProjectiles = 35 }, null);
        // 40 grenades: more than a snapshot carries; the world's limit (35) detonates the oldest, so detonations occur too.
        for (int n = 0; n < 40; n++) { world.Step(Shooter(InputButtons.Fire, -0.3f + n * 0.01f)); for (int k = 0; k < 6; k++) world.Step(Idle); }
        world.Step(Shooter(InputButtons.Fire, -0.2f));
        Assert.NotEmpty(world.RecentDetonations);
        var views = GameServer.ProjectilesFor(world, Vector3.Zero);
        Assert.True(views.Length <= Protocol.MaxSnapshotProjectiles);
        Assert.True((views[0].Flags & ProjectileFlags.Detonated) != 0);
        var live = views.Where(v => (v.Flags & ProjectileFlags.Detonated) == 0).ToList();
        var nearest = world.Projectiles.OrderBy(p => p.Position.LengthSquared()).ThenBy(p => p.Id).Take(live.Count).Select(p => p.Id).ToList();
        Assert.Equal(nearest, live.Select(v => v.Id));
    }

    [Fact]
    public void The_config_hash_covers_charge_projectile_and_detonation_fields()
    {
        var config = ConfigWith(Launcher, ChargeTests.PlasmaGun);
        var seen = new HashSet<ulong> { config.Hash() };
        Assert.Equal(config.Hash(), ConfigWith(Launcher with { }, ChargeTests.PlasmaGun with { }).Hash());
        var charge = ChargeTests.PlasmaGun.Charge!;
        SimConfig[] variants =
        [
            ConfigWith(Launcher with { DetonationDelays = [0f, 0.25f] }, ChargeTests.PlasmaGun),
            ConfigWith(Launcher with { Projectile = Grenade with { MaxActive = 5 } }, ChargeTests.PlasmaGun),
            ConfigWith(Launcher with { Projectile = Grenade with { Splash = GrenadeBlast with { Radius = 7f } } }, ChargeTests.PlasmaGun),
            ConfigWith(Launcher, ChargeTests.PlasmaGun with { Charge = charge with { AmmoToStart = 14 } }),
            ConfigWith(Launcher, ChargeTests.PlasmaGun with { Charge = charge with { Stages = [charge.Stages[0], charge.Stages[1] with { MinTime = 1.2f }] } }),
            config with { ProjectileOwnerGrace = 0.2f }, config with { ProjectileRestitution = 0.4f }, config with { MaxProjectiles = 64 },
        ];
        foreach (var v in variants) Assert.True(seen.Add(v.Hash()), "a change left the hash unchanged");
    }

    // ---------------------------------------------------------------- review fixes (F1–F6) and further paths

    /// <summary>A very slow dart (1 cm per tick) with 10 direct damage, detonating on impact; no blast damage.</summary>
    static readonly ProjectileSpec Crawler = Dart(splash: GrenadeBlast with { Damage = GrenadeBlast.Damage with { Damage = 0f } }) with { Speed = 0.3f };

    [Theory]
    [InlineData(1f, true)]     // walking into it: hit (before the fix it started inside the target and was skipped)
    [InlineData(-1f, false)]   // control: walking away
    public void A_target_walking_into_a_slow_projectile_is_hit(float moveY, bool hit)
    {
        var world = WorldWith(ConfigWith(Weapon("crawler", Crawler, directDamage: 10f)), null, new Vector3(0, 3, 0));
        world.Step(Shooter(InputButtons.Fire));
        var hits = new List<HitEvent>();
        for (int k = 0; k < 60; k++)
        {
            world.Step(id => id == 2 ? new PlayerInput(0, 0, moveY, MathF.PI, InputButtons.None) : Idle(id));
            hits.AddRange(world.LastHits);
        }
        if (!hit) { Assert.Empty(hits); Assert.Single(world.Projectiles); return; }
        Assert.Contains(hits, h => h.Target == 2 && h.Shooter == 1 && !h.Splash && h.Projectile > 0 && h.HealthDamage + h.ShieldDamage > 0f);
        Assert.DoesNotContain(hits, h => h.Target == 1 && !h.Splash);   // never the owner it started inside
        Assert.Empty(world.Projectiles);
    }

    [Fact]
    public void A_projectile_that_stops_on_a_player_strikes_them_once_and_falls()
    {
        var pebble = Dart(splash: GrenadeBlast) with { DetonateOnCollision = false, Gravity = [new(0f, 1f)], Speed = 30f };
        var world = WorldWith(ConfigWith(Weapon("pebble", pebble, directDamage: 10f)), null, new Vector3(0, 6, 0));
        world.Step(Shooter(InputButtons.Fire, 0.05f));
        var hits = new List<HitEvent>();
        for (int k = 0; k < 60; k++) { world.Step(Idle); hits.AddRange(world.LastHits); }
        Assert.Single(hits, h => h.Target == 2);
        var p = Assert.Single(world.Projectiles);
        Assert.Equal(ProjectileMotion.Stuck, p.Motion);   // fell to the ground and rests there
        Assert.Equal(2, p.Struck);
        Assert.InRange(p.Position.Z, 0f, 0.05f);
    }

    [Fact]
    public void Holding_reload_after_a_detonation_does_not_reload_until_released()
    {
        var world = LauncherWorld(out _);
        FireGrenade(world);
        for (int k = 0; k < 30; k++) world.Step(Idle);
        for (int k = 0; k < 20; k++)
        {
            world.Step(Shooter(InputButtons.Reload));
            Assert.Equal(0f, world.Players[1].ReloadLeft);
            Assert.True(world.Players[1].DetonateHeld);
        }
        Assert.Empty(world.Projectiles);
        world.Step(Idle);
        Assert.False(world.Players[1].DetonateHeld);
        world.Step(Shooter(InputButtons.Reload));   // control: a new press reloads
        Assert.True(world.Players[1].ReloadLeft > 0f);
        Assert.False(world.Players[1].DetonateHeld);
    }

    [Fact]
    public void Snapshots_never_list_one_16_bit_id_twice_and_the_world_avoids_live_collisions()
    {
        ProjectileState Live(int id, float y) => new(id, 1, 0, 0, new Vector3(0, y, 0), Vector3.Zero) { Motion = ProjectileMotion.Stuck };
        var views = GameServer.ProjectilesFor([Live(5, 1f), Live(5 + 65536, 2f), Live(9, 3f)], [new DetonationEvent(1, 9 + 65536, 1, 0, 0, Vector3.One, 6f, 0, 0, 0, 0)], Vector3.Zero);
        Assert.Equal([9, 5], views.Select(v => v.Id));
        Assert.Equal(ProjectileFlags.Detonated, views[0].Flags);
        Assert.Equal(new Vector3(0, 1, 0), views[1].Position);   // the nearer of the two
        Assert.NotNull(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [], Projectiles: views))));

        var world = WorldWith(ConfigWith(Weapon("launcher", Grenade with { MaxActive = 0 })), null);
        world.Step(Shooter(InputButtons.Fire, -0.3f));
        int first = Assert.Single(world.Projectiles).Id;
        typeof(World).GetField("_nextProjectileId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(world, first + 65536);
        for (int k = 0; k < 7; k++) world.Step(Idle);
        world.Step(Shooter(InputButtons.Fire, -0.3f));
        Assert.Equal(2, world.Projectiles.Count);
        Assert.Equal(first + 65537, world.Projectiles[1].Id);   // first + 65536 was skipped
    }

    [Fact]
    public void A_blast_stuck_to_a_player_goes_off_where_the_player_is_now()
    {
        var sticky = Grenade with { MaxActive = 1, Gravity = [new(0f, 0f)] };
        var world = WorldWith(ConfigWith(Weapon("sticky", sticky) with { DetonationDelays = [] }), null, new Vector3(0, 8, 0));
        world.Step(Shooter(InputButtons.Fire));
        for (int k = 0; k < 10; k++) world.Step(Idle);
        var g = Assert.Single(world.Projectiles);
        Assert.Equal(ProjectileMotion.Attached, g.Motion);
        var offset = g.Position - world.Players[2].Position;
        // The host walks while the shooter launches a second grenade: the first (over the limit) goes off at once.
        world.Step(id => id == 2 ? new PlayerInput(0, 1, 0, MathF.PI, InputButtons.None) : new PlayerInput(0, 0, 0, 0, InputButtons.Fire));
        var blast = Assert.Single(world.LastDetonations);
        Assert.Equal(g.Id, blast.Projectile);
        Assert.True(Vector3.Distance(world.Players[2].Position + offset, blast.Position) < 1e-4f, $"{blast.Position}");
        Assert.NotEqual(g.Position, blast.Position);   // control: the host did move this tick
    }

    [Fact]
    public void Unusable_points_and_delays_are_refused()
    {
        var classes = new[] { new ClassLoadout("test", 0, -1, []) };
        Assert.NotNull(SmContent.GameplayTuning.WithClasses(new SimConfig(), [Launcher], classes));   // control
        WeaponSpec[] bad =
        [
            Launcher with { Projectile = Grenade with { Gravity = [new(5f, 0f), new(1f, 1f)] } },
            Launcher with { Projectile = Grenade with { Splash = GrenadeBlast with { FallOff = [new(4f, 1f), new(2f, 0.5f)] } } },
            Launcher with { DetonationDelays = [0f, float.NaN] },
            Launcher with { DetonationDelays = [-0.1f] },
            Launcher with { DetonationDelays = [float.PositiveInfinity] },
        ];
        foreach (var b in bad) Assert.Null(SmContent.GameplayTuning.WithClasses(new SimConfig(), [b], classes));
        Assert.NotNull(SmContent.GameplayTuning.WithClasses(new SimConfig(), [Launcher with { Projectile = Grenade with { Gravity = [new(5f, 0f), new(5f, 1f)] } }], classes));   // equal distances: a step
    }

    [Fact]
    public void A_direct_detonation_damage_and_the_blast_both_land_on_the_player_struck()
    {
        var cannon = Dart(splash: GrenadeBlast) with { DetonateDamage = new DamageSpec(200f, 0f, 0f, 0f, 1f, 1f, true) };   // shield only
        var world = WorldWith(ConfigWith(Weapon("cannon", cannon)) with { ShieldMax = 500f }, null, new Vector3(0, 6, 0));
        world.Step(Shooter(InputButtons.Fire));
        var hits = new List<HitEvent>();
        for (int k = 0; k < 10; k++) { world.Step(Idle); hits.AddRange(world.LastHits); }
        var direct = Assert.Single(hits, h => h.Target == 2 && !h.Splash);
        Assert.Equal((200f, 0f), (direct.ShieldDamage, direct.HealthDamage));
        var blast = Assert.Single(hits, h => h.Target == 2 && h.Splash);
        Assert.Equal((0f, 75f), (blast.ShieldDamage, blast.HealthDamage));   // the blast's shield multipliers are 0
        Assert.Equal(300f, world.Players[2].Shield, 3);
        Assert.Equal(925f, world.Players[2].Health, 3);
    }

    [Fact]
    public void A_projectile_stuck_to_a_player_who_dies_or_leaves_rests_and_an_owner_who_leaves_takes_theirs()
    {
        // Player 3 stands next to the host with a lethal knife (hit on the press tick).
        var lethal = new MeleeStep(1f, 0f, 0.4f, 0.6f, 0.5f, 0.7f, 5000f, 0f, 3.5f);
        var config = ConfigWith(Weapon("sticky", Grenade with { Gravity = [new(0f, 0f)] })) with { Classes = [new ClassLoadout("test", 0, -1, [lethal])] };
        var world = WorldWith(config, Level(), new Vector3(0, 8, 0), new Vector3(3, 8, 0));
        world.Step(Shooter(InputButtons.Fire));
        for (int k = 0; k < 10; k++) world.Step(Idle);
        var g = Assert.Single(world.Projectiles);
        Assert.Equal((ProjectileMotion.Attached, 2), (g.Motion, g.AttachedTo));
        world.Step(id => id == 3 ? new PlayerInput(0, 0, 0, MathF.PI / 2f, InputButtons.Melee) : Idle(id));   // facing −X, toward the host
        Assert.False(world.Players[2].Alive);
        world.Step(Idle);
        g = Assert.Single(world.Projectiles);
        Assert.Equal((ProjectileMotion.Flying, -1), (g.Motion, g.AttachedTo));   // sim/sticky-launcher: it drops off the corpse and falls
        for (int k = 0; k < 60; k++) world.Step(Idle);
        g = Assert.Single(world.Projectiles);   // it landed on the floor and stays there, still undetonated
        Assert.Equal((ProjectileMotion.Stuck, -1), (g.Motion, g.AttachedTo));
        Assert.Equal(0.02f, g.Position.Z, 3);
        var landed = g.Position;
        for (int k = 0; k < 30; k++) world.Step(Idle);
        Assert.Equal(landed, world.Projectiles[0].Position);
        Assert.Empty(world.LastDetonations);

        // The host leaves: the same. The owner leaves: its projectiles go.
        world = WorldWith(config, Level(), new Vector3(0, 8, 0));
        world.Step(Shooter(InputButtons.Fire));
        for (int k = 0; k < 10; k++) world.Step(Idle);
        world.Remove(2);
        Assert.Equal((ProjectileMotion.Flying, -1), (world.Projectiles[0].Motion, world.Projectiles[0].AttachedTo));
        world.Remove(1);
        Assert.Empty(world.Projectiles);
    }

    [Theory]
    [InlineData(0.1f, true)]    // after the grace a projectile coming back strikes its owner
    [InlineData(10f, false)]    // control: within the grace it passes through
    public void The_owner_can_be_struck_by_its_own_projectile_after_the_grace(float grace, bool struck)
    {
        var boomerang = Grenade with { Gravity = [new(0f, 0f)], MaxBounces = 1, MaxActive = 0, Sticky = false };
        var world = WorldWith(ConfigWith(Weapon("boomerang", boomerang)) with { ProjectileOwnerGrace = grace }, Level(wallY: 3f));
        world.Step(Shooter(InputButtons.Fire));
        for (int k = 0; k < 20; k++) world.Step(Idle);
        var g = Assert.Single(world.Projectiles);
        Assert.Equal(1, g.Bounces);
        if (struck) Assert.Equal(1, g.Struck);   // it hit its owner and stopped
        else { Assert.Equal(ProjectileMotion.Flying, g.Motion); Assert.True(g.Position.Y < -1f); }
    }

    [Fact]
    public void A_projectile_leaving_the_world_bound_is_removed()
    {
        var fast = Dart() with { Speed = 1e5f, DetonateWhenLifetimeOver = false };
        var world = WorldWith(ConfigWith(Weapon("fast", fast)), null);
        world.Step(Shooter(InputButtons.Fire, 1.2f));
        Assert.Single(world.Projectiles);   // control: still there after the launch tick
        var blasts = new List<DetonationEvent>();
        for (int k = 0; k < 60; k++) { world.Step(Idle); blasts.AddRange(world.LastDetonations); }
        Assert.Empty(world.Projectiles);
        Assert.Empty(blasts);
    }

    [Theory]
    [InlineData(-0.35f, false)]   // over the shoulder, beside the head: inside the hit-scan cylinder, outside the hit shapes
    [InlineData(0f, true)]        // control: straight at the target
    public void A_slow_projectile_passing_over_a_stationary_targets_shoulder_is_not_hit(float targetX, bool hit)
    {
        // 0.1 m per tick, climbing to 1.85 m at y = 3 (the target's centre line); no gravity, no blast damage.
        var dart = Crawler with { Speed = 3f };
        var world = WorldWith(ConfigWith(Weapon("dart", dart, directDamage: 10f)), null, new Vector3(targetX, 3, 0));
        float pitch = MathF.Atan(0.25f / 3f);
        world.Step(Shooter(InputButtons.Fire, pitch));
        var hits = new List<HitEvent>();
        for (int k = 0; k < 60; k++) { world.Step(Idle); hits.AddRange(world.LastHits); }
        if (hit) { Assert.Contains(hits, h => h.Target == 2 && !h.Splash); return; }
        Assert.DoesNotContain(hits, h => h.Target == 2);
        Assert.True(Assert.Single(world.Projectiles).Position.Y > 3.5f);   // it flew past
    }

    [Fact]
    public void A_sticky_shot_on_a_walking_player_sticks_where_it_struck()
    {
        // The host walks away from the shooter (+Y) at run speed: the offset is measured from where the host was at impact,
        // so the projectile sits on the body capsule's surface (0.45 m from its axis segment), not a part of a tick's walk behind it.
        var sticky = Grenade with { Lifetime = 0f, Gravity = [new(0f, 0f)], MaxActive = 0 };
        var world = WorldWith(ConfigWith(Weapon("sticky", sticky)), null, new Vector3(0, 8, 0));
        world.Step(Shooter(InputButtons.Fire));
        ProjectileState g = default;
        for (int k = 0; k < 20; k++)
        {
            world.Step(id => id == 2 ? new PlayerInput(0, 0, -1f, MathF.PI, InputButtons.None) : Idle(id));   // backwards: +Y
            g = Assert.Single(world.Projectiles);
            if (g.Motion == ProjectileMotion.Attached) break;
        }
        Assert.Equal(ProjectileMotion.Attached, g.Motion);
        // Distance from the body capsule's axis segment (BodyBottom + r to BodyTop - r).
        var c = world.Config;
        float axisZ = Math.Clamp(g.AttachOffset.Z, c.BodyBottom + c.BodyRadius, c.BodyTop - c.BodyRadius);
        float fromAxis = Vector3.Distance(g.AttachOffset, new Vector3(0, 0, axisZ));
        output.WriteLine($"offset {g.AttachOffset}, {fromAxis} from the capsule axis");
        Assert.InRange(fromAxis, c.BodyRadius - 0.01f, c.BodyRadius + 0.01f);
    }

    [Fact]
    public void The_projectile_limit_is_clamped()
    {
        var world = WorldWith(ConfigWith(Weapon("launcher", Grenade with { MaxActive = 0 })) with { MaxProjectiles = 0 }, null);
        world.Step(Shooter(InputButtons.Fire, -0.3f));
        Assert.Single(world.Projectiles);   // 0 acts as 1: the launch still happens
        for (int k = 0; k < 7; k++) world.Step(Idle);
        int first = world.Projectiles[0].Id;
        world.Step(Shooter(InputButtons.Fire, -0.3f));
        Assert.Equal(first, Assert.Single(world.LastDetonations).Projectile);
        Assert.Single(world.Projectiles);
        Assert.Equal(4096, World.MaxProjectilesLimit);
    }

    [Theory]
    [InlineData(0f, true)]      // a tick boundary at y = 9, beside the head inside the hit-scan cylinder: the next tick hits the head
    [InlineData(0.5f, false)]   // control: the same shot passing beside the target
    public void A_fast_shot_whose_tick_ends_beside_the_head_still_hits_it(float targetX, bool hit)
    {
        // 3 m per tick from the eye (1.6 m) toward the head centre (1.95 m) 9.35 m away: the third tick ends at y ≈ 9,
        // 0.35 m from the head centre (outside the 0.25 m head sphere, inside the 0.45 m cylinder).
        var world = WorldWith(ConfigWith(Weapon("dart", Dart() with { Speed = 90f }, directDamage: 10f)), null, new Vector3(targetX, 9.35f, 0));
        world.Step(Shooter(InputButtons.Fire, MathF.Atan(0.35f / 9.35f)));
        world.Step(Idle);
        world.Step(Idle);
        var p = Assert.Single(world.Projectiles);
        Assert.InRange(p.Position.Y, 8.95f, 9.05f);
        var local = p.Position - world.Players[2].Position;
        if (targetX == 0f)
        {
            Assert.Equal(HitZone.None, Combat.InsideHitShapes(p.Position, world.Players[2].Position, world.Config));
            Assert.True(Combat.InsideHitVolumes(p.Position, world.Players[2].Position, world.Config), $"{local}");
        }
        var hits = new List<HitEvent>();
        for (int k = 0; k < 3; k++) { world.Step(Idle); hits.AddRange(world.LastHits); }
        if (hit) Assert.Equal(HitZone.Head, Assert.Single(hits, h => h.Target == 2 && !h.Splash).Zone);
        else Assert.DoesNotContain(hits, h => h.Target == 2 && !h.Splash);
    }

    sealed class ScriptedTransport : IClientTransport
    {
        public Queue<byte[]> Incoming { get; } = new();
        public List<byte[]> Sent { get; } = new();
        public void Send(byte[] packet) => Sent.Add(packet);
        public bool TryReceive(out byte[] packet) => Incoming.TryDequeue(out packet!);
    }
}
