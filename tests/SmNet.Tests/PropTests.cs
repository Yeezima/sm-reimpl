using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Movable props as solid cover (M39; Manufactorum's green ammo crates): they join both collision meshes after the level, stop
/// walking and flying players, every shot and thrown item, and a hit-scan shot with a <see cref="PierceSpec"/> passes through
/// them (points and costs from the wargear's <c>penetration</c>) and damages players behind them with its exit cone. Synthetic
/// geometry and weapons only.
/// </summary>
public class PropTests
{
    const int CrateSurface = 4;

    /// <summary>A flat floor (surface 0) 100 m across at z = 0.</summary>
    static StaticMesh Floor() => new([new(-50, -50, 0), new(50, -50, 0), new(50, 50, 0), new(-50, 50, 0)], [0, 1, 2, 0, 2, 3], [0, 0]);

    /// <summary>A closed axis-aligned box as a prop (12 triangles).</summary>
    internal static CollisionProp Box(string name, Vector3 min, Vector3 max, int surface = CrateSurface)
    {
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++) v[i] = new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        int[] indices = [0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5];
        return new CollisionProp(name, v, indices, surface);
    }

    /// <summary>A crate across the line from the origin to +Y: x −2..2, y from <paramref name="y"/> 1.2 m deep, 2.5 m tall.</summary>
    static CollisionProp Crate(float y, string name = "crate") => Box(name, new Vector3(-2f, y, 0f), new Vector3(2f, y + 1.2f, 2.5f));

    static CollisionWorld Level(params CollisionProp[] props) => new(Floor(), Floor(), props: props);

    /// <summary>The lascannon's basic penetration on the install: 10 points, 1 per prop, exit cone 15 m, 1°, 50 damage, every player.</summary>
    static readonly PierceSpec Lascannon = new(10f, 1f, new ConeSpec(15f, 1f, 0f, 0f, new DamageSpec(50f, 0f, 1f, 1f, 1f, 1f, false), [], []), true);
    /// <summary>The stalker bolter's: 10 points, 5 per prop, exit cone 10 m, 3°, 20 damage.</summary>
    static readonly PierceSpec Stalker = new(10f, 5f, new ConeSpec(10f, 3f, 0f, 0f, new DamageSpec(20f, 0f, 1f, 1f, 1f, 1f, false), [], []), true);

    static SimConfig Armed(PierceSpec? pierce)
    {
        var c = new SimConfig { HealthMax = 1e6f, ShieldMax = 0f };
        return c with { Weapons = [c.Weapons[0] with { Pierce = pierce, Damage = 100f, MaxRange = 250f, FallOffStart = 250f, FallOffEnd = 250f, HeadMultiplier = 1f, BodyMultiplier = 1f }] };
    }

    static PlayerInput FireAt(World world, int shooter, Vector3 point)
    {
        var eye = Combat.EyePosition(world.Players[shooter], world.Config);
        var to = point - eye;
        return new PlayerInput(world.Tick, 0, 0, MathF.Atan2(-to.X, to.Y), InputButtons.Fire).WithShotDirection(to);
    }

    /// <summary>Player 1 at the origin fires once at player 2's chest; returns the shot and the hits on player 2.</summary>
    static (ShotEvent Shot, List<HitEvent> Hits) Fire(SimConfig c, CollisionWorld level, Vector3 target)
    {
        var world = new World(c, level, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(target, MathF.PI)]);
        world.Spawn(1); world.Spawn(2);
        var aim = world.Players[2].Position + new Vector3(0, 0, HitVolumes.CentreHeight(c));
        world.Step(id => id == 1 ? FireAt(world, 1, aim) : new PlayerInput(world.Tick, 0, 0, MathF.PI, InputButtons.None));
        return (Assert.Single(world.LastShots), world.LastHits.Where(h => h.Target == 2).ToList());
    }

    // ---------------------------------------------------------------- the collision world

    [Fact]
    public void Props_join_both_meshes_after_the_level_and_their_triangles_are_named()
    {
        var plain = new CollisionWorld(Floor(), Floor());
        Assert.Equal(plain.ContentHash, Level().ContentHash);   // no props: as before M39
        var world = Level(Crate(3f), Crate(10f, "second"));
        Assert.Equal((2 + 24, 2 + 24), (world.Character.TriangleCount, world.Weapons.TriangleCount));
        Assert.Equal((-1, 0, 0, 1, 1, -1, -1), (world.PropOfShotTriangle(1), world.PropOfShotTriangle(2), world.PropOfShotTriangle(13),
            world.PropOfShotTriangle(14), world.PropOfShotTriangle(25), world.PropOfShotTriangle(26), world.PropOfShotTriangle(-1)));
        Assert.Equal((0, CrateSurface, CrateSurface), (world.Weapons.MaterialOf(0), world.Weapons.MaterialOf(2), world.Weapons.MaterialOf(25)));
        Assert.NotEqual(plain.ContentHash, world.ContentHash);
        Assert.NotEqual(world.ContentHash, Level(Crate(3f), Crate(10.5f, "second")).ContentHash);
        Assert.Equal(world.ContentHash, Level(Crate(3f), Crate(10f, "second")).ContentHash);
        // Solid for both sides' movement and for shots; a level mesh without materials gives the props theirs anyway.
        var eye = new Vector3(0, 0, 1f);
        foreach (var mesh in new[] { world.Character, world.CharacterFor(Team.SpaceMarines), world.CharacterFor(Team.Chaos), world.Weapons })
            Assert.Equal(3f, mesh.Raycast(eye, Vector3.UnitY, 20f)!.Value.Distance, 4);
        var bare = new StaticMesh([new(-50, -50, 0), new(50, -50, 0), new(50, 50, 0)], [0, 1, 2]);
        var mixed = new CollisionWorld(bare, bare, props: [Crate(3f)]);
        Assert.Equal((-1, CrateSurface, false), (mixed.Weapons.MaterialOf(0), mixed.Weapons.MaterialOf(1), mixed.Character.HasMaterials));
        Assert.Throws<ArgumentException>(() => Level(new CollisionProp("broken", [Vector3.Zero], [0, 0, 1], 0)));
        Assert.Throws<ArgumentException>(() => Level(new CollisionProp("nan", [new(float.NaN, 0, 0), Vector3.UnitX, Vector3.UnitY], [0, 1, 2], 0)));
    }

    [Fact]
    public void A_crate_stops_players_walking_into_it_and_flying_into_it_with_a_jump_pack()
    {
        foreach (var level in new[] { Level(), Level(Crate(3f)) })
        {
            var c = new SimConfig();
            var world = new World(c, level, [new SpawnPoint(Vector3.Zero, 0f)]);
            world.Spawn(1);
            for (int k = 0; k < 3 * c.TickRate; k++) world.Step(_ => new PlayerInput(world.Tick, 0f, 1f, 0f, InputButtons.None));
            float y = world.Players[1].Position.Y;
            if (level.Props.Count == 0) Assert.True(y > 8f, $"open floor: {y}");
            else Assert.InRange(y, 2f, 3f - c.CapsuleRadius + 0.01f);   // stopped by the crate's face at y = 3
        }

        // A jump pack's flight (every movement mode uses the side's character mesh): a prop wall taller than the flight is not passed.
        var tall = Level(Box("wall", new Vector3(-20f, 4f, 0f), new Vector3(20f, 5f, 40f)));
        var flying = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, JumpPackTests.Config);
        float highest = 0f;
        for (int k = 0; k < 120; k++)
        {
            flying = Combat.StepPlayer(flying, new PlayerInput(0, 0f, 1f, 0f, k < 20 ? InputButtons.Jump : InputButtons.None), JumpPackTests.Config, out _, tall);
            highest = MathF.Max(highest, flying.Position.Z);
            Assert.True(flying.Position.Y < 4f - JumpPackTests.Config.CapsuleRadius + 0.01f, $"tick {k}: {flying.Position}");
        }
        Assert.True(highest > 1f, $"the pack lifted off ({highest} m)");
    }

    // ---------------------------------------------------------------- shots

    [Fact]
    public void A_shot_without_penetration_stops_at_a_crate_and_names_its_surface()
    {
        var c = Armed(null);
        var (open, openHits) = Fire(c, Level(), new Vector3(0f, 8f, 0f));
        Assert.Equal((ShotHit.Body, 2), (open.Hit, open.Target));
        Assert.Equal(100f, Assert.Single(openHits).HealthDamage, 3);

        var (shot, hits) = Fire(c, Level(Crate(3f)), new Vector3(0f, 8f, 0f));
        Assert.Equal((ShotHit.World, CrateSurface, 0), (shot.Hit, shot.Surface, shot.Target));
        Assert.Equal(3f, shot.End.Y, 2);
        Assert.Empty(hits);
    }

    [Fact]
    public void A_piercing_shot_passes_a_crate_and_its_exit_cone_hits_the_player_behind_it()
    {
        var c = Armed(Lascannon);
        var (shot, hits) = Fire(c, Level(Crate(3f)), new Vector3(0f, 8f, 0f));
        Assert.Equal((ShotHit.Body, 2, -1), (shot.Hit, shot.Target, shot.Surface));
        var hit = Assert.Single(hits);
        Assert.Equal(50f, hit.HealthDamage, 3);   // the exit's damage, not the shot's 100
        Assert.Equal(1, hit.Shooter);
        Assert.InRange(shot.End.Y, 7f, 8f);

        // In front of the crate the shot's own damage applies; past the exit cone's 15 m nothing is hit.
        var (near, nearHits) = Fire(c, Level(Crate(6f)), new Vector3(0f, 4f, 0f));
        Assert.Equal((ShotHit.Body, 100f), (near.Hit, Assert.Single(nearHits).HealthDamage));
        var (far, farHits) = Fire(c, Level(Crate(3f)), new Vector3(0f, 22f, 0f));
        Assert.Empty(farHits);
        Assert.NotEqual(ShotHit.Body, far.Hit);

        // The level's own collision is never pierced: a wall of the level mesh stops the same shot.
        var floorAndWall = new StaticMesh([new(-50, -50, 0), new(50, -50, 0), new(50, 50, 0), new(-50, 50, 0),
            new(-20, 3, 0), new(20, 3, 0), new(20, 3, 5), new(-20, 3, 5)], [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]);
        var (walled, walledHits) = Fire(c, new CollisionWorld(floorAndWall, floorAndWall), new Vector3(0f, 8f, 0f));
        Assert.Equal(ShotHit.World, walled.Hit);
        Assert.Empty(walledHits);
    }

    [Fact]
    public void Penetration_points_and_costs_limit_how_many_crates_a_shot_passes()
    {
        var three = Level(Crate(3f, "a"), Crate(6f, "b"), Crate(9f, "c"));
        var origin = new Vector3(0f, 0f, 1f);
        var stalker = three.TraceShot(origin, Vector3.UnitY, 100f, Stalker);   // 10 points, 5 a crate: two crates
        Assert.Equal([0, 1], stalker.Pierced.Select(p => p.Prop));
        Assert.Equal(9f, stalker.Stop!.Value.Distance, 3);
        Assert.Equal(2, three.PropOfShotTriangle(stalker.Stop.Value.Triangle));
        Assert.Equal((3f, 4.2f, true), (MathF.Round(stalker.Pierced[0].Entry, 3), MathF.Round(stalker.Pierced[0].Exit, 3), stalker.Pierced[0].IntoOpen));
        var las = three.TraceShot(origin, Vector3.UnitY, 100f, Lascannon);   // 1 a crate: all three, then the range
        Assert.Equal([0, 1, 2], las.Pierced.Select(p => p.Prop));
        Assert.Null(las.Stop);
        Assert.Empty(three.TraceShot(origin, Vector3.UnitY, 100f, null).Pierced);
        Assert.Equal(3f, three.TraceShot(origin, Vector3.UnitY, 100f, null).Stop!.Value.Distance, 3);
        // A cost of 0 with points left is free (the charged lascannon); 0 points pierce nothing (the heavy bolter).
        Assert.Equal(3, three.TraceShot(origin, Vector3.UnitY, 100f, Stalker with { PropCost = 0f }).Pierced.Count);
        Assert.Empty(three.TraceShot(origin, Vector3.UnitY, 100f, Stalker with { Points = 0f, PropCost = 0f }).Pierced);

        // Overlapping crates (a stack) are entered and left one by one; only the last exit is into the open.
        var stack = Level(Crate(3f, "low"), Box("overlap", new Vector3(-2f, 3.8f, 0f), new Vector3(2f, 5f, 2.5f)));
        var through = stack.TraceShot(origin, Vector3.UnitY, 100f, Lascannon);
        Assert.Equal([(0, 3f, 4.2f, false), (1, 3.8f, 5f, true)], through.Pierced.Select(p => (p.Prop, MathF.Round(p.Entry, 3), MathF.Round(p.Exit, 3), p.IntoOpen)));

        // In the world: the stalker's exit cone reaches a player behind two crates, not behind three.
        var c = Armed(Stalker);
        var (two, twoHits) = Fire(c, Level(Crate(2f, "a"), Crate(4f, "b")), new Vector3(0f, 9f, 0f));
        Assert.Equal((ShotHit.Body, 20f), (two.Hit, Assert.Single(twoHits).HealthDamage));
        var (blocked, blockedHits) = Fire(c, Level(Crate(2f, "a"), Crate(4f, "b"), Crate(6f, "c")), new Vector3(0f, 9f, 0f));
        Assert.Equal((ShotHit.World, CrateSurface), (blocked.Hit, blocked.Surface));
        Assert.Empty(blockedHits);
    }

    [Fact]
    public void An_exit_for_enemies_and_neutrals_spares_allies_even_with_friendly_fire()
    {
        foreach (bool everyone in new[] { true, false })
        {
            var basic = Armed(Lascannon with { ExitHitsAllies = everyone });
            var c = basic with { Classes = [basic.Classes[0] with { Side = Team.SpaceMarines }] };
            var world = new World(c, Level(Crate(3f)), [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, 8f, 0f), MathF.PI)])
            { Rules = new MatchRules { TimeLimit = 0f, FriendlyFire = true } };
            world.Spawn(1); world.Spawn(2);
            Assert.Equal(world.Players[1].Team, world.Players[2].Team);
            var aim = world.Players[2].Position + new Vector3(0, 0, HitVolumes.CentreHeight(c));
            world.Step(id => id == 1 ? FireAt(world, 1, aim) : new PlayerInput(world.Tick, 0, 0, MathF.PI, InputButtons.None));
            Assert.Equal(everyone ? 1 : 0, world.LastHits.Count(h => h.Target == 2));
        }
    }

    // ---------------------------------------------------------------- thrown items and the hash

    [Fact]
    public void A_grenade_bounces_off_a_crate_like_a_wall()
    {
        var config = GrenadeTests.Config;
        var world = new World(config, Level(Box("crate wall", new Vector3(-20f, 6f, 0f), new Vector3(20f, 7.2f, 3f))), [new SpawnPoint(Vector3.Zero, 0f)]);
        world.Spawn(1, 0, equipmentChoice: 0);
        world.Step(_ => new PlayerInput(world.Tick, 0, 0, 0f, InputButtons.Grenade));
        var track = new List<ProjectileState>();
        for (int k = 0; k < 400; k++)
        {
            world.Step(_ => new PlayerInput(world.Tick, 0, 0, 0f, InputButtons.None));
            if (world.Projectiles.Count > 0) track.Add(world.Projectiles[0]);
            else if (track.Count > 0) break;
        }
        var bounced = track.First(p => p.Bounces == 1);
        Assert.True(bounced.Velocity.Y < 0f, $"{bounced.Velocity}");   // it came back off the crate
        Assert.InRange(bounced.Position.Y, 5f, 6f);
        Assert.All(track, p => Assert.True(p.Position.Y < 6f, $"{p.Position}"));
    }

    [Fact]
    public void Penetration_is_part_of_the_config_hash()
    {
        Assert.NotEqual(Armed(null).Hash(), Armed(Lascannon).Hash());
        Assert.NotEqual(Armed(Lascannon).Hash(), Armed(Stalker).Hash());
        Assert.NotEqual(Armed(Lascannon).Hash(), Armed(Lascannon with { ExitHitsAllies = false }).Hash());
        Assert.Equal(Armed(Lascannon).Hash(), Armed(Lascannon with { }).Hash());
    }
}
