using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Ork O4 in the simulation: a class's own body (speeds, health and shield, hit volumes; <see cref="SimConfig.For"/>), the Ork's side
/// (<see cref="World.AssignSide"/>, either team), the side-only dodge hop, and that configs without class bodies (every marine and Chaos
/// config) hash exactly as before.
/// </summary>
public class OrkSimTests
{
    const int Tactical = 0, ChaosTactical = 1, Ork = 2;

    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 20f, 0.15f, 30, 270, 0.5f, 250f, 245f, 250f, 1f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly float[] HopProfile = [0f, 0.5f, 1f, 1.5f, 2f, 2.5f, 3f, 3.5f, 4f, 4f, 4f];
    static EvadeMove Side(string name, float sign) => new(name, HopProfile.Select(d => new Vector2(sign * d, 0f)).ToArray(), 1f, 0.6f, 0.6f, 0.7f, 0.7f, 1f);
    static readonly EvadeSpec SideHop = new("ork_coop_boy_evade_ranged", Side("R", 1f), Side("L", -1f), Side("L", -1f), Side("R", 1f))
    {
        MinInterval = 0.8f, SideOnly = true, AnimationSet = "ork_side_hop", TargetableModeTime = 0.5f,
    };
    /// <summary>A short body (head at 1.2 m, top at 1.4 m) against the placeholder marine (body to 1.7 m, head at 1.95 m).</summary>
    static readonly HitVolume[] Short =
    [
        HitVolume.Sphere("Head", HitZone.Head, new(0f, 0f, 1.2f), 0.2f),
        HitVolume.Capsule("Spine", HitZone.Body, new(0f, 0f, 0.3f), new(0f, 0f, 0.9f), 0.3f),
    ];
    static readonly BodySpec OrkBody = new("ork - boy (test)", 5f, 6.5f, 4.5f, 8f, 3f, 3f, 3f, 2f, 120f, 5f, 5f, 50f, 7f, 33f) { HitVolumes = Short };

    static ClassLoadout Class(string name, Team side, string role, int evade, int body = -1) =>
        new(name, 0, -1, SimConfig.PlaceholderMeleeSteps) { PrimaryChoices = [0], Side = side, Role = role, Evade = evade, Body = body };

    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle],
        Classes = [Class("sm - tactical", Team.SpaceMarines, "tactical", 0), Class("csm - tactical", Team.Chaos, "tactical", 0), Class("ork - boy", Team.None, "ork", 1, body: 0)],
        Evades = [EvadeTests.Roll, SideHop],
        Bodies = [OrkBody],
        RespawnTime = 0.1f,
    };

    static PlayerState Fresh(int cls, Vector3 at = default) => Combat.Respawned(new PlayerState(1, at, Vector3.Zero, 0f, true) { Class = cls }, Config);

    static void Step(ref PlayerState s, InputButtons b, float mx = 0f, float my = 0f, float yaw = 0f) =>
        s = Combat.StepPlayer(s, new PlayerInput(0, mx, my, yaw, b), Config, out _);

    // ---------------------------------------------------------------- unchanged configs

    [Fact]
    public void ConfigsWithoutClassBodiesHashAsBefore()
    {
        // Pinned before Ork O4 (main cda1272) and computed again on main a822478 without O4: the same values, so every existing config hashes as it did.
        // Re-pinned for the owner's blind grenade by facing (2026-10-28: SimConfig.BlindViewHalfAngle is hashed, so every config's hash moved once;
        // before: B9AE0B048FDE504D, 192D6A8FD5B02B56, DA9A87CAC1337ED8, C4400831F020CBF3). Class bodies still add nothing for a config without them.
        Assert.Equal(0xA57B097C29D78EABUL, new SimConfig().Hash());
        Assert.Equal(0x24C00662A26C4F7AUL, EvadeTests.Config.Hash());
        Assert.Equal(0x9214E4F961AAB3ABUL, EvadeTests.HopConfig.Hash());
        Assert.Equal(0x0CCE9A750CE539F1UL, new SimConfig { HitVolumes = [HitVolume.Sphere("Head", HitZone.Head, new(0, 0, 1.9f), 0.2f)] }.Hash());
    }

    [Fact]
    public void BodiesTheirIndexAndTheSideOnlyHopChangeTheHash()
    {
        var plain = Config with { Bodies = [], Classes = Config.Classes.Select(c => c with { Body = -1 }).ToArray() };
        Assert.NotEqual(plain.Hash(), (plain with { Bodies = [OrkBody] }).Hash());
        Assert.NotEqual((plain with { Bodies = [OrkBody] }).Hash(), Config.Hash());
        Assert.NotEqual(Config.Hash(), (Config with { Bodies = [OrkBody with { BackSpeed = 4f }] }).Hash());
        Assert.NotEqual(Config.Hash(), (Config with { Bodies = [OrkBody with { HitVolumes = null }] }).Hash());
        Assert.NotEqual(Config.Hash(), (Config with { Evades = [EvadeTests.Roll, SideHop with { SideOnly = false }] }).Hash());
    }

    // ---------------------------------------------------------------- the class's body

    [Fact]
    public void ForGivesTheClassBodyAndTheConfigItselfOtherwise()
    {
        Assert.Same(Config, Config.For(Tactical));
        Assert.Same(Config, Config.For(-1));
        Assert.Same(Config, Config.For(99));
        var ork = Config.For(Ork);
        Assert.NotSame(Config, ork);
        Assert.Same(ork, Config.For(Ork));   // cached
        Assert.Same(ork, ork.For(Ork));      // a copy gives the base's copies
        Assert.Same(Config, ork.For(Tactical));   // and the base for a class without a body
        Assert.Equal((5f, 6.5f, 4.5f, 8f, 120f, 50f), (ork.RunSpeed, ork.StrafeSpeed, ork.BackSpeed, ork.SprintSpeed, ork.HealthMax, ork.ShieldMax));
        Assert.Same(Short, ork.HitVolumes);
        Assert.Equal((Config.EyeHeight, Config.CapsuleRadius, Config.Weapons), (ork.EyeHeight, ork.CapsuleRadius, ork.Weapons));   // the rest stays
        // A body without volumes takes the config's; a changed config starts afresh.
        var volumes = new[] { HitVolume.Sphere("Head", HitZone.Head, new(0, 0, 1.9f), 0.2f) };
        var other = Config with { HitVolumes = volumes, Bodies = [OrkBody with { HitVolumes = null }] };
        Assert.Same(volumes, other.For(Ork).HitVolumes);
        Assert.Equal(5f, (Config with { RunSpeed = 9f }).For(Ork).RunSpeed);
        Assert.Equal(9f, (Config with { RunSpeed = 9f }).For(Tactical).RunSpeed);
    }

    [Fact]
    public void AnOrkSpawnsAndRegeneratesWithItsOwnHealthAndShield()
    {
        var marine = Fresh(Tactical);
        var ork = Fresh(Ork);
        Assert.Equal((100f, 100f), (marine.Health, marine.Shield));
        Assert.Equal((120f, 50f), (ork.Health, ork.Shield));
        Assert.Equal(120f, Loadouts.Body(Config, ork).HealthMax);
        ork = ork with { Health = 60f, Shield = 0f, SinceDamage = 100f };
        for (int i = 0; i < 600; i++) Step(ref ork, InputButtons.None);   // 5 per s from 60: 12 s
        Assert.Equal((120f, 50f), (ork.Health, ork.Shield));   // regenerates to its own maxima
    }

    [Fact]
    public void AnOrkMovesAtItsOwnSpeeds()
    {
        float After(int cls, float mx, float my, InputButtons b = InputButtons.None)
        {
            var s = Fresh(cls);
            for (int i = 0; i < 30; i++) Step(ref s, b, mx, my);
            return new Vector2(s.Velocity.X, s.Velocity.Y).Length();
        }
        Assert.Equal(Config.BackSpeed, After(Tactical, 0f, -1f), 3);
        Assert.Equal(4.5f, After(Ork, 0f, -1f), 3);
        Assert.Equal(6.5f, After(Ork, 1f, 0f), 3);
        Assert.Equal(Config.StrafeSpeed, After(Tactical, 1f, 0f), 3);
        Assert.Equal(8f, After(Ork, 0f, 1f, InputButtons.Sprint), 3);
        var walked = Fresh(Ork);
        for (int i = 0; i < 30; i++) walked = Movement.Step(walked, new PlayerInput(0, 0f, 1f, 0f, InputButtons.None), Config);   // Movement alone too
        Assert.Equal(5f, walked.Velocity.Length(), 3);
    }

    [Fact]
    public void ShotsHitEachTargetByItsOwnVolumes()
    {
        // A level shot from the eye (1.6 m): through the placeholder marine's body (to 1.7 m), over the short Ork (top 1.4 m).
        foreach (var (cls, hit) in new[] { (Tactical, true), (Ork, false), (ChaosTactical, true) })
        {
            var world = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, 6f, 0f), 0f)]);
            world.Spawn(1, Tactical);
            world.Spawn(2, cls);
            world.Step(id => id == 1 ? new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire) : new PlayerInput(0, 0f, 0f, 0f, InputButtons.None));
            Assert.Equal(hit, world.LastHits.Any(h => h.Target == 2));
        }
        // Lower, the Ork is hit in its own head.
        var head = Combat.HitTest(new Vector3(0f, 0f, 1.2f), Vector3.UnitY, new Vector3(0f, 6f, 0f), Config.For(Ork), 100f);
        Assert.Equal(HitZone.Head, head.Zone);
        Assert.Equal(HitZone.Body, Combat.HitTest(new Vector3(0f, 0f, 1.2f), Vector3.UnitY, new Vector3(0f, 6f, 0f), Config, 100f).Zone);
    }

    /// <summary>A world of the shooter (player 1, class 0 at the origin, facing +Y) and a target (player 2) of <paramref name="cls"/> at <paramref name="at"/>.</summary>
    static World Pair(SimConfig c, int cls, Vector3 at, int shooterClass = Tactical)
    {
        var world = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(at, 0f)]);
        world.Spawn(1, shooterClass);
        world.Spawn(2, cls);
        return world;
    }

    /// <summary>Steps <paramref name="ticks"/> ticks, player 1 pressing <paramref name="press"/> in the first only (with <paramref name="origin"/> as its shot origin offset); every hit on player 2.</summary>
    static List<HitEvent> Run(World world, InputButtons press, int ticks, float originZ = 0f)
    {
        var hits = new List<HitEvent>();
        for (int t = 0; t < ticks; t++)
        {
            int tick = t;
            world.Step(id => id == 1 && tick == 0 ? new PlayerInput(0, 0f, 0f, 0f, press, ShotOriginZ: originZ) : new PlayerInput(0, 0f, 0f, 0f, InputButtons.None));
            hits.AddRange(world.LastHits.Where(h => h.Target == 2));
        }
        return hits;
    }

    [Fact]
    public void ProjectilesHitEachTargetByItsOwnVolumes()
    {
        // A level dart from the eye (1.6 m), no splash: it strikes the placeholder marine (body to 1.7 m) and flies over the short Ork (top 1.4 m).
        var dart = new ProjectileSpec("ebps\\test\\dart", 30f, 0f, "raycast", 0f, [new(0f, 0f)], true, true, false, 0, 0f, 0f, 0, null, null);
        var gun = new WeaponSpec("test_dart_gun", WeaponSlot.Ranged, TriggerKind.SemiAuto, 10f, 0.2f, 10, 20, 0.6f, 150f, 150f, 150f, 1f, 1f, 1f, 0f, 0f)
        { Delivery = DeliveryKind.Projectile, Projectile = dart };
        var c = Config with { Weapons = [gun] };
        var marine = Run(Pair(c, ChaosTactical, new Vector3(0f, 6f, 0f)), InputButtons.Fire, 15);
        Assert.Contains(marine, h => h.Projectile != 0 && !h.Splash);
        Assert.Empty(Run(Pair(c, Ork, new Vector3(0f, 6f, 0f)), InputButtons.Fire, 15));
    }

    [Fact]
    public void BlastsMeasureToEachPlayersOwnChest()
    {
        // A tiny Ork (volumes 0 to 0.8 m: chest 0.4 m) against the placeholder marine (chest 0.85 m). The heavy stomp (a melee blast, no hit of its
        // own) goes off at the attacker's chest with a 1.05 m radius; a target 1 m to the side is inside for a marine chest at the same height
        // (1.0 m) and outside for the Ork's (sqrt(1 + 0.45²) = 1.10 m), and an Ork's stomp starts at its own chest.
        HitVolume[] tiny = [HitVolume.Sphere("Head", HitZone.Head, new(0f, 0f, 0.6f), 0.2f), HitVolume.Capsule("Spine", HitZone.Body, new(0f, 0f, 0.1f), new(0f, 0f, 0.4f), 0.1f)];
        var stomp = new SplashSpec(1.05f, new DamageSpec(50f, 0f, 1f, 1f, 1f, 1f, false), [new(0f, 1f), new(2f, 1f)], false, true, "target_all");
        MeleeStep[] steps = [new MeleeStep(1f, 0.3f, 0f, 0f, 1f, 1f, 0f, 0f, 0f) { Stomp = stomp }];
        var c = Config with
        {
            Classes = Config.Classes.Select(k => k with { Melee = steps }).ToArray(),
            Bodies = [OrkBody with { HitVolumes = tiny }],
        };
        Assert.Equal(0.4f, HitVolumes.CentreHeight(c.For(Ork)), 4);
        Assert.Equal(0.85f, HitVolumes.CentreHeight(c.For(Tactical)), 4);
        var side = new Vector3(1f, 0f, 0f);
        Assert.Contains(Run(Pair(c, ChaosTactical, side), InputButtons.Melee, 20), h => h.Splash && h.Melee && h.HealthDamage + h.ShieldDamage > 0f);
        Assert.Empty(Run(Pair(c, Ork, side), InputButtons.Melee, 20));                               // the Ork's chest is lower: outside
        Assert.Empty(Run(Pair(c, ChaosTactical, side, shooterClass: Ork), InputButtons.Melee, 20));    // an Ork's stomp starts at its own chest
        Assert.NotEmpty(Run(Pair(c, Ork, side, shooterClass: Ork), InputButtons.Melee, 20));          // chest to chest: inside
    }

    [Fact]
    public void ConesCoverEachTargetByItsOwnVolumes()
    {
        // A narrow cone (8°, no start length) fired level from 2.0 m (the eye raised 0.4 m by the shot origin): 6 m out it spans about 1.58 to 2.42 m,
        // the placeholder marine's head (1.95 m) but nothing of the short Ork (top 1.4 m). Coverage of any sample deals the full damage here.
        var cone = new ConeSpec(10f, 8f, 0f, 0f, new DamageSpec(40f, 0f, 1f, 1f, 1f, 1f, false), [new(0f, 1f), new(10f, 1f)], [new(0f, 1f), new(1f, 1f)]);
        var gun = new WeaponSpec("test_cone", WeaponSlot.Ranged, TriggerKind.SemiAuto, 40f, 0.3f, 5, 10, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f)
        { Delivery = DeliveryKind.Cone, Cone = cone };
        var c = Config with { Weapons = [gun] };
        var at = new Vector3(0f, 6f, 0f);
        Assert.True(Cones.Coverage(cone, new Vector3(0f, 0f, 2f), Vector3.UnitY, at, c.For(Tactical)) > 0f);
        Assert.Equal(0f, Cones.Coverage(cone, new Vector3(0f, 0f, 2f), Vector3.UnitY, at, c.For(Ork)));
        Assert.Contains(Run(Pair(c, ChaosTactical, at), InputButtons.Fire, 2, originZ: 0.4f), h => h.Cone);
        Assert.Empty(Run(Pair(c, Ork, at), InputButtons.Fire, 2, originZ: 0.4f));
    }

    // ---------------------------------------------------------------- sides

    static World Match(MatchRules? rules = null) =>
        new(Config, null, Enumerable.Range(0, 20).Select(i => new SpawnPoint(new Vector3(i * 3f, 0f, 0f), 0f)).ToArray()) { Rules = rules ?? new MatchRules { TimeLimit = 0f } };

    [Fact]
    public void AnOrkJoinsTheSideItAsksForUnderTheBalanceRule()
    {
        var w = Match();
        Assert.Equal(Team.Chaos, w.AssignSide(Ork, Team.Chaos));
        Assert.Equal(Team.Chaos, w.Spawn(1, Ork, team: w.AssignSide(Ork, Team.Chaos)).Team);
        Assert.Equal(Team.SpaceMarines, w.AssignSide(Ork, Team.Chaos));   // Chaos already has more: the other side
        Assert.Equal(Team.SpaceMarines, w.AssignSide(Ork));               // no request: the smaller side
        w.Spawn(2, Tactical);
        Assert.Equal(Team.Chaos, w.AssignSide(Ork, Team.Chaos));          // even again
        Assert.Equal(Team.SpaceMarines, w.AssignSide(Ork, Team.SpaceMarines));
        Assert.Equal(Team.SpaceMarines, w.AssignSide(Tactical, Team.Chaos));   // a class with a side keeps it (AssignClass moves those)
        Assert.Equal(Team.Chaos, w.AssignSide(ChaosTactical));
        // A side request on a sided class is ignored by Spawn; an Ork without one goes to the smaller side.
        Assert.Equal(Team.SpaceMarines, w.Spawn(3, Tactical, team: Team.Chaos).Team);
        Assert.Equal(Team.Chaos, w.Spawn(4, Ork).Team);
        Assert.Equal(Team.None, new World(Config).AssignSide(Ork, Team.Chaos));   // no rules: no sides
        Assert.Equal(Team.None, new World(Config).Spawn(1, Ork, team: Team.Chaos).Team);
    }

    [Fact]
    public void AFullSideOrTheHonourOptionDecide()
    {
        var full = Match(new MatchRules { TimeLimit = 0f, MaxPerSide = 1 });
        full.Spawn(1, ChaosTactical);
        full.Spawn(2, Tactical);
        Assert.Equal(Team.SpaceMarines, full.AssignSide(Ork, Team.Chaos));
        var honour = Match(new MatchRules { TimeLimit = 0f, HonorSideRequests = true });
        honour.Spawn(1, Ork, team: Team.Chaos);
        honour.Spawn(2, Ork, team: honour.AssignSide(Ork, Team.Chaos));
        Assert.Equal(Team.Chaos, honour.Players[2].Team);   // kept although Chaos had more
    }

    [Fact]
    public void ClassChangesKeepTheTeam()
    {
        var w = Match();
        w.Spawn(1, Ork, team: Team.Chaos);
        w.Spawn(2, Tactical);
        Assert.Equal(ChaosTactical, w.SetNextLoadout(1, new LoadoutChoice(Tactical, -1, -1, -1, -1, -1, -1))!.Value.Class);   // the side's tactical
        Assert.Equal(Ork, w.SetNextLoadout(2, new LoadoutChoice(Ork, -1, -1, -1, -1, -1, -1))!.Value.Class);                 // the Ork on any side
        Assert.Equal(Ork, w.ClassForSide(Ork, Team.Chaos));
        Assert.Equal(Ork, w.ClassForSide(Ork, Team.SpaceMarines));
    }

    // ---------------------------------------------------------------- the dodge hop

    [Theory]
    [InlineData(1f, 0f, EvadeDirection.Right)]
    [InlineData(-1f, 0f, EvadeDirection.Left)]
    [InlineData(0f, 1f, EvadeDirection.Right)]     // forward: the default side
    [InlineData(0f, -1f, EvadeDirection.Right)]    // back: likewise
    [InlineData(0f, 0f, EvadeDirection.Right)]
    [InlineData(-0.7f, -0.7f, EvadeDirection.Left)] // a diagonal hops to its side
    [InlineData(0.7f, 0.7f, EvadeDirection.Right)]
    public void TheHopGoesOnlySideways(float mx, float my, EvadeDirection expected)
    {
        var s = Fresh(Ork);
        Step(ref s, InputButtons.Jump, mx, my);
        Assert.Equal((1, expected), (s.EvadeTicks, s.EvadeDirection));
        for (int i = 0; i < 29; i++) Step(ref s, InputButtons.None);
        float x = s.Position.X;
        Assert.Equal(expected == EvadeDirection.Right ? 4f : -4f, x, 2);   // along the clip's side path
        Assert.Equal(0f, s.Position.Y, 3);
    }

    [Fact]
    public void ForwardOrBackHopsTheWayTheBodyIsAlreadyMoving()
    {
        Assert.Equal(EvadeDirection.Left, Evade.SideOf(0f, -3f, 0f, 0f));   // drifting left (yaw 0: right is +X)
        Assert.Equal(EvadeDirection.Right, Evade.SideOf(0f, 3f, 0f, 0f));
        Assert.Equal(EvadeDirection.Right, Evade.SideOf(0f, -0.2f, 0f, 0f));   // too slow to count
        Assert.Equal(EvadeDirection.Left, Evade.SideOf(0f, 0f, -3f, MathF.PI / 2f));   // yaw 90°: right is +Y
        Assert.Equal(EvadeDirection.Right, Evade.SideOf(0.5f, -3f, 0f, 0f));   // the stick wins
        Assert.Equal(EvadeDirection.Right, Evade.SideOf(float.NaN, float.NaN, 0f, 0f));
        var s = Fresh(Ork);
        for (int i = 0; i < 20; i++) Step(ref s, InputButtons.None, -1f, 0f);   // strafing left
        Step(ref s, InputButtons.Jump, 0f, 1f);                                 // then forward + jump
        Assert.Equal(EvadeDirection.Left, s.EvadeDirection);
    }

    [Fact]
    public void TheTacticalStillRolls()
    {
        var s = Fresh(Tactical);
        Step(ref s, InputButtons.Jump, 0f, 1f);
        Assert.Equal(EvadeDirection.Forward, s.EvadeDirection);
        var t = Fresh(Tactical);
        Step(ref t, InputButtons.Jump, -0.7f, -0.7f);
        Assert.Equal(EvadeDirection.Back, t.EvadeDirection);   // four directions (this roll has no diagonals): never a side hop
    }
}
