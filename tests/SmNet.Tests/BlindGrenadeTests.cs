using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M37: the blind grenade (the files' "krak"; owner's knowledge of the original, 2026-10-28). Its blasts deal no damage, record
/// no hit and start no reaction or knockback; they blind every living player whose eye is within the blast's radius and seen
/// from the blast point (weapon collision): enemies, allies and the thrower alike. Shaped like the install (r 8: 3.5 s, r 30:
/// 2 s); synthetic specs. The M37 tests run the original's distance-only rule (<see cref="SimConfig.BlindViewHalfAngle"/> 180, "SM1:
/// by distance"); since the owner's decision of 2026-10-28 the default blinds only players facing the blast (the facing tests below).
/// </summary>
public class BlindGrenadeTests(ITestOutputHelper output)
{
    const int Thrower = 0, Other = 1, Ally = 2;

    static readonly DamageSpec None = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    internal static readonly SplashSpec Inner = new(8f, None, [], true, false, "target_all") { Blindness = 3.5f, BlindOnly = true };
    internal static readonly SplashSpec Outer = new(30f, None, [], true, false, "target_all") { Blindness = 2f, BlindOnly = true };

    /// <summary>A blind grenade that goes off where it first touches something (so the blast point is known: straight down, just ahead of the thrower).</summary>
    static readonly ThrownSpec Blind = new("test_krak", 2, 2, 1f, 10f,
        new ProjectileSpec("ebps\\test\\krak", 47.5f, 7f, "raycast", 0f, [], true, true, false, 0, 0f, 1.5f, 0, null, Inner) { ExtraSplashes = [Outer] });

    /// <summary>The same item thrown as the install's rigid body (bounce, rest, 1 s ground fuse).</summary>
    static readonly ThrownSpec RigidBlind = new("test_krak_rigid", 2, 2, 1f, 10f, GrenadeTests.FragBall with { Splash = Inner, ExtraSplashes = [Outer] });

    static readonly WeaponSpec Gun = new("test_gun", WeaponSlot.Ranged, TriggerKind.FullAuto, 10f, 0.1f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);

    /// <param name="viewHalfAngle">180: SM1's distance-only rule (the M37 tests); <see cref="SimConfig.BlindViewHalfAngle"/>'s default: by facing.</param>
    static SimConfig ConfigWith(ThrownSpec item, float viewHalfAngle = 180f) => new()
    {
        BlindViewHalfAngle = viewHalfAngle,
        Weapons = [Gun],
        Thrown = [item],
        Classes =
        [
            new ClassLoadout("thrower", 0, -1, []) { EquipmentChoices = [0], Side = Team.SpaceMarines },
            new ClassLoadout("other", 0, -1, []) { Side = Team.Chaos },
            new ClassLoadout("ally", 0, -1, []) { Side = Team.SpaceMarines },
        ],
    };

    /// <summary>A ground plane, and optionally a wall across x = <paramref name="wallX"/> (y from 3 to 20, up to 30 m).</summary>
    static CollisionWorld Level(float? wallX)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        Quad(new(-80, -80, 0), new(80, -80, 0), new(80, 80, 0), new(-80, 80, 0));
        if (wallX is { } x) Quad(new(x, 3, -1), new(x, 20, -1), new(x, 20, 30), new(x, 3, 30));
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    /// <summary>Player 1 (the thrower, at the origin facing +Y) throws straight down once; runs until the grenade went off.</summary>
    /// <param name="yaws">Each other player's view yaw (radians; default 0, facing +Y); they look level.</param>
    static (DetonationEvent Blast, List<BlindEvent> Blinded, List<HitEvent> Hits, List<ReactionEvent> Reactions) ThrowDown(World world, int maxTicks = 400,
        Func<int, float>? yaws = null)
    {
        var blinded = new List<BlindEvent>(); var hits = new List<HitEvent>(); var reactions = new List<ReactionEvent>();
        DetonationEvent? blast = null;
        for (int k = 0; k < maxTicks && blast is null; k++)
        {
            world.Step(id => new PlayerInput(0, 0, 0, id == 1 ? 0f : yaws?.Invoke(id) ?? 0f, id == 1 && k == 0 ? InputButtons.Grenade : InputButtons.None, id == 1 ? -1.4f : 0f));
            blinded.AddRange(world.LastBlindings); hits.AddRange(world.LastHits); reactions.AddRange(world.LastReactions);
            if (world.LastDetonations.Count > 0) blast = world.LastDetonations[0];
        }
        Assert.NotNull(blast);
        return (blast!.Value, blinded, hits, reactions);
    }

    [Fact]
    public void It_blinds_everyone_in_sight_by_range_and_never_through_cover_or_beyond_30_m_with_no_damage_or_reaction()
    {
        // Spots along +X at y 0.33 (the blast lands about 0.32 m ahead of the thrower), and one behind a wall at x = 10 (y 3..20).
        var spots = new (string What, Vector3 At)[]
        {
            ("thrower", Vector3.Zero),
            ("5 m", new(5f, 0.33f, 0)),
            ("7.5 m (eye 7.67 m)", new(7.5f, 0.33f, 0)),
            ("8 m (eye about 8.15 m)", new(0f, -7.68f, 0)),
            ("20 m", new(-20f, 0.33f, 0)),
            ("29.9 m", new(0f, 30.2f, 0)),
            ("35 m", new(-35f, 0.33f, 0)),
            ("behind the wall", new(15f, 8f, 0)),
            ("beside the wall (control)", new(-15f, 8f, 0)),
        };
        var config = ConfigWith(Blind);
        var world = new World(config, Level(10f), spots.Select(s => new SpawnPoint(s.At, 0f)).ToArray());
        world.Spawn(1, Thrower, equipmentChoice: 0);
        for (int i = 1; i < spots.Length; i++) world.Spawn(i + 1, Other);
        for (int i = 0; i < spots.Length; i++) Assert.Equal(spots[i].At, world.Players[i + 1].Position);
        var before = world.Players.ToDictionary(p => p.Key, p => (p.Value.Health, p.Value.Shield));

        var (blast, blinded, hits, reactions) = ThrowDown(world);
        output.WriteLine($"blast at {blast.Position} tick {blast.Tick}: {blast.Victims} hit");
        Assert.InRange(blast.Position.Y, 0.25f, 0.40f);
        Assert.Equal(0.05f, blast.Position.Z, 3);   // the ground plus the blast offset

        // No damage, no hit, no reaction, no knockback: for everyone, the thrower included.
        Assert.Empty(hits);
        Assert.Empty(reactions);
        Assert.Equal((0, 0f, 0f), (blast.Victims, blast.ShieldDamage, blast.HealthDamage));
        foreach (var (id, p) in world.Players)
        {
            Assert.Equal(before[id], (p.Health, p.Shield));
            Assert.Equal((ReactionKind.None, 0, 0), (p.Reaction, p.PushDistance, p.PushTicks));
        }

        // Blindness by the eye's distance from the blast point: within 8 m the inner blast's 3.5 s (105 ticks), to 30 m the outer's 2 s (60).
        int[] expected = [105, 105, 105, 60, 60, 60, 0, 0, 60];
        for (int i = 0; i < spots.Length; i++)
        {
            var eye = Combat.EyePosition(world.Players[i + 1], config);
            output.WriteLine($"  {spots[i].What}: eye {Vector3.Distance(eye, blast.Position):F2} m, blind {world.Players[i + 1].BlindTicks} ticks (expected {expected[i]})");
        }
        // The blast's tick already counted nothing down (blindness starts in the detonation tick, after the player steps).
        Assert.Equal(expected, Enumerable.Range(1, spots.Length).Select(id => world.Players[id].BlindTicks));
        Assert.Equal([1, 2, 3, 4, 5, 6, 9], blinded.Select(b => b.Target).Distinct().Order());
        Assert.All(blinded, b => Assert.Equal(1, b.Source));
    }

    [Fact]
    public void Allies_and_the_thrower_are_blinded_too_whatever_friendly_fire_says_and_blindness_then_runs_out()
    {
        var config = ConfigWith(Blind);
        var world = new World(config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(4f, 0, 0), 0f), new SpawnPoint(new Vector3(-12f, 0, 0), 0f)])
            { Rules = new MatchRules { TimeLimit = 0f, FriendlyFire = false } };
        world.Spawn(1, Thrower, equipmentChoice: 0);   // marines
        world.Spawn(2, Ally);                          // marines
        world.Spawn(3, Other);                         // chaos
        Assert.Equal((Team.SpaceMarines, Team.SpaceMarines, Team.Chaos), (world.Players[1].Team, world.Players[2].Team, world.Players[3].Team));
        Assert.Equal((new Vector3(4f, 0, 0), new Vector3(-12f, 0, 0)), (world.Players[2].Position, world.Players[3].Position));
        ThrowDown(world);
        Assert.Equal((105, 105, 60), (world.Players[1].BlindTicks, world.Players[2].BlindTicks, world.Players[3].BlindTicks));
        for (int k = 0; k < 60; k++) world.Step(_ => new PlayerInput(0, 0, 0, 0f, InputButtons.None));
        Assert.Equal((45, 45, 0), (world.Players[1].BlindTicks, world.Players[2].BlindTicks, world.Players[3].BlindTicks));
    }

    [Fact]
    public void The_rigid_grenade_rests_goes_off_on_its_fuse_and_blinds_by_the_point_it_went_off_at()
    {
        var config = ConfigWith(RigidBlind);
        var spots = new[] { Vector3.Zero, new Vector3(6f, 10f, 0), new Vector3(-6f, -6f, 0), new Vector3(0, 45f, 0) };
        var world = new World(config, Level(null), spots.Select(p => new SpawnPoint(p, 0f)).ToArray());
        world.Spawn(1, Thrower, equipmentChoice: 0);
        for (int i = 1; i < spots.Length; i++) world.Spawn(i + 1, Other);
        var (blast, blinded, hits, reactions) = ThrowDown(world);
        output.WriteLine($"rigid blast at {blast.Position}");
        Assert.Empty(hits);
        Assert.Empty(reactions);
        foreach (var (id, p) in world.Players)
        {
            float d = Vector3.Distance(Combat.EyePosition(p, config), blast.Position);
            int want = d <= 8f ? 105 : d <= 30f ? 60 : 0;
            output.WriteLine($"  player {id}: eye {d:F2} m, blind {p.BlindTicks} (expected {want})");
            Assert.Equal(want, p.BlindTicks);
            Assert.Equal((config.HealthMax, config.ShieldMax), (p.Health, p.Shield));
        }
        Assert.Contains(blinded, b => b.Target == 1);
    }

    [Fact]
    public void A_longer_blindness_is_kept_and_the_flag_changes_the_config_hash()
    {
        var config = ConfigWith(Blind);
        var s = Reaction.Blind(Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), config), config, 3.5f);
        Assert.Equal(105, s.BlindTicks);
        Assert.Equal(105, Reaction.Blind(s, config, 2f).BlindTicks);   // the outer blast does not shorten the inner one
        Assert.NotEqual(config.Hash(), ConfigWith(Blind with { Projectile = Blind.Projectile with { Splash = Inner with { BlindOnly = false } } }).Hash());
        Assert.True(Blind.IsBlindGrenade);
        Assert.Equal("blind grenade", Blind.PlayerName);
        Assert.False(GrenadeTests.Frag.IsBlindGrenade);
        Assert.Null(GrenadeTests.Frag.PlayerName);
    }

    // ---------------------------------------------------------------- by facing (owner's decision 2026-10-28; change from SM1)

    /// <summary>The yaw that looks from <paramref name="from"/> toward <paramref name="to"/> (<see cref="Combat.AimDirection"/>: −sin, cos).</summary>
    static float YawTowards(Vector3 from, Vector3 to) => MathF.Atan2(-(to.X - from.X), to.Y - from.Y);

    [Fact]
    public void By_default_only_players_facing_the_blast_are_blinded_with_the_same_bands_and_line_of_sight()
    {
        var config = ConfigWith(Blind, new SimConfig().BlindViewHalfAngle);
        Assert.Equal(60f, config.BlindViewHalfAngle);
        var blastNear = new Vector3(0f, 0.33f, 0f);   // where ThrowDown's grenade lands (about 0.32 m ahead of the thrower)
        var spots = new (string What, Vector3 At, float Yaw, int Expected)[]
        {
            ("thrower, looking down at it", Vector3.Zero, 0f, 105),
            ("5 m, facing it", new(5f, 0.33f, 0), MathF.PI / 2f, 105),
            ("5 m, back turned", new(-5f, 0.33f, 0), MathF.PI / 2f, 0),
            ("5 m, side-on (90 degrees)", new(0f, -5f, 0), -MathF.PI / 2f, 0),
            ("20 m, facing it", new(-20f, 0.33f, 0), -MathF.PI / 2f, 60),
            ("35 m, facing it (beyond 30 m)", new(-35f, 0.33f, 0), -MathF.PI / 2f, 0),
            ("behind the wall, facing it", new(15f, 8f, 0), YawTowards(new(15f, 8f, 0), blastNear), 0),
            ("beside the wall, facing it (control)", new(-15f, 8f, 0), YawTowards(new(-15f, 8f, 0), blastNear), 60),
        };
        var world = new World(config, Level(10f), spots.Select(s => new SpawnPoint(s.At, 0f)).ToArray());
        world.Spawn(1, Thrower, equipmentChoice: 0);
        for (int i = 1; i < spots.Length; i++) world.Spawn(i + 1, Other);
        var (blast, blinded, hits, _) = ThrowDown(world, yaws: id => spots[id - 1].Yaw);
        Assert.Empty(hits);
        for (int i = 0; i < spots.Length; i++)
        {
            var p = world.Players[i + 1];
            var eye = Combat.EyePosition(p, config);
            output.WriteLine($"  {spots[i].What}: eye {Vector3.Distance(eye, blast.Position):F2} m, faces {World.Faces(p, eye, blast.Position, config.BlindViewHalfAngle)}, " +
                $"blind {p.BlindTicks} ticks (expected {spots[i].Expected})");
        }
        Assert.Equal(spots.Select(s => s.Expected), Enumerable.Range(1, spots.Length).Select(id => world.Players[id].BlindTicks));
        Assert.Equal([1, 2, 5, 8], blinded.Select(b => b.Target).Distinct().Order());

        // The same throw under SM1's rule (180: by distance) blinds the back-turned and side-on players too.
        var sm1 = new World(ConfigWith(Blind, 180f), Level(10f), spots.Select(s => new SpawnPoint(s.At, 0f)).ToArray());
        sm1.Spawn(1, Thrower, equipmentChoice: 0);
        for (int i = 1; i < spots.Length; i++) sm1.Spawn(i + 1, Other);
        ThrowDown(sm1, yaws: id => spots[id - 1].Yaw);
        Assert.Equal([105, 105, 105, 105, 60, 0, 0, 60], Enumerable.Range(1, spots.Length).Select(id => sm1.Players[id].BlindTicks));
    }

    [Fact]
    public void Facing_is_the_angle_between_the_view_with_its_pitch_and_the_eye_to_blast_direction()
    {
        var player = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true);   // yaw 0: looking along +Y, level
        var eye = new Vector3(0, 0, 1.6f);
        Vector3 At(float degreesRight, float up = 0f)
        {
            float a = degreesRight * MathF.PI / 180f;
            return eye + new Vector3(MathF.Sin(a) * 10f, MathF.Cos(a) * 10f, up);
        }
        Assert.True(World.Faces(player, eye, At(0f), 60f));
        Assert.True(World.Faces(player, eye, At(59f), 60f));
        Assert.True(World.Faces(player, eye, At(-59f), 60f));
        Assert.False(World.Faces(player, eye, At(61f), 60f));
        Assert.False(World.Faces(player, eye, At(180f), 60f));
        // Pitch counts: looking up 70 degrees, a level blast ahead is 70 degrees off the view; looking at it, it is faced.
        Assert.False(World.Faces(player with { Pitch = 70f * MathF.PI / 180f }, eye, At(0f), 60f));
        Assert.True(World.Faces(player with { Pitch = -45f * MathF.PI / 180f }, eye, eye + new Vector3(0, 5f, -5f), 10f));
        // The yaw turns the cone: facing +X (yaw -90 degrees), a blast to the right is ahead.
        Assert.True(World.Faces(player with { Yaw = -MathF.PI / 2f }, eye, At(90f), 30f));
        // A blast at the eye is faced; 180 (or more, or not finite) is SM1's every direction.
        Assert.True(World.Faces(player, eye, eye, 1f));
        Assert.True(World.Faces(player, eye, At(180f), 180f));
        Assert.True(World.Faces(player, eye, At(180f), float.NaN));
    }

    [Fact]
    public void The_view_half_angle_is_in_the_config_hash()
    {
        Assert.Equal(60f, new SimConfig().BlindViewHalfAngle);
        Assert.NotEqual(ConfigWith(Blind, 60f).Hash(), ConfigWith(Blind, 180f).Hash());
        Assert.NotEqual(new SimConfig().Hash(), new SimConfig { BlindViewHalfAngle = 180f }.Hash());
    }
}
