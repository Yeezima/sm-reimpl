using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// M39 follow-up: shots that pierce players (the lascannon passes up to 10, the stalker bolter and Kraken Bolts 2: points and
/// the player blueprint's materials), per-prop spans (touching, coplanar and overlapping crates, edge grazes, an origin inside a
/// prop), the perk hook, and what else props stop (melee, the melta cone), with lag compensation, kill credit, the shield and
/// health split, the head zone, a charged stage and the once-per-player exit rule. Synthetic geometry and weapons only.
/// </summary>
public class PierceTests
{
    static StaticMesh Floor() => new([new(-80, -80, 0), new(80, -80, 0), new(80, 80, 0), new(-80, 80, 0)], [0, 1, 2, 0, 2, 3], [0, 0]);
    static CollisionWorld Level(params CollisionProp[] props) => new(Floor(), Floor(), props: props);
    static CollisionProp Crate(float y, float depth = 1.2f, string name = "crate") => PropTests.Box(name, new Vector3(-2f, y, 0f), new Vector3(2f, y + depth, 2.5f));

    static ConeSpec ExitCone(float length, float angle, float damage) => new(length, angle, 0f, 0f, new DamageSpec(damage, 0f, 1f, 1f, 1f, 1f, false), [], []);
    /// <summary>The install's lascannon: 10 points; props, bodies and heads 1; exit 15 m, 1°, 50, every player.</summary>
    internal static readonly PierceSpec Lascannon = new(10f, 1f, ExitCone(15f, 1f, 50f), true) { BodyCost = 1f, HeadCost = 1f };
    /// <summary>The install's stalker bolter: 10 points; props 5, bodies 4, heads 3; exit 10 m, 3°, 20.</summary>
    internal static readonly PierceSpec Stalker = new(10f, 5f, ExitCone(10f, 3f, 20f), true) { BodyCost = 4f, HeadCost = 3f };
    /// <summary>The bolter's Kraken Bolts entry: 2 points; props, bodies and heads 1; exit 10 m, 3°, 16, enemies and neutrals.</summary>
    internal static readonly PierceSpec Kraken = new(2f, 1f, ExitCone(10f, 3f, 16f), false) { BodyCost = 1f, HeadCost = 1f };

    static SimConfig Armed(PierceSpec? pierce, float shield = 0f, float health = 1e6f, float head = 1f)
    {
        var c = new SimConfig { HealthMax = health, ShieldMax = shield, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f };
        return c with
        {
            Weapons = [c.Weapons[0] with { Pierce = pierce, Damage = 100f, MaxRange = 250f, FallOffStart = 250f, FallOffEnd = 250f, HeadMultiplier = head, BodyMultiplier = 1f,
                ShieldHeadMultiplier = 1f, ShieldBodyMultiplier = 1f }],
        };
    }

    /// <summary>Player 1 at the origin, the others at <paramref name="targets"/> facing it; player 1 fires once along +Y at eye height (bodies).</summary>
    static (World World, ShotEvent Shot, List<HitEvent> Hits) Line(SimConfig c, CollisionWorld? level, Vector3[] targets, Vector3? aim = null)
    {
        var world = new World(c, level, [new SpawnPoint(Vector3.Zero, 0f), .. targets.Select(t => new SpawnPoint(t, MathF.PI))]);
        for (int i = 0; i <= targets.Length; i++) world.Spawn(i + 1);
        var eye = Combat.EyePosition(world.Players[1], c);
        var to = (aim ?? eye + Vector3.UnitY * 100f) - eye;
        world.Step(id => id == 1 ? new PlayerInput(world.Tick, 0, 0, MathF.Atan2(-to.X, to.Y), InputButtons.Fire).WithShotDirection(to)
            : new PlayerInput(world.Tick, 0, 0, MathF.PI, InputButtons.None));
        return (world, Assert.Single(world.LastShots), world.LastHits.ToList());
    }

    static Vector3[] Row(int count, float first = 3f, float step = 2f) => [.. Enumerable.Range(0, count).Select(k => new Vector3(0f, first + step * k, 0f))];

    // ---------------------------------------------------------------- players

    [Fact]
    public void The_lascannon_passes_ten_players_and_stops_in_the_eleventh_each_taking_the_full_shot()
    {
        var (_, shot, hits) = Line(Armed(Lascannon), Level(), Row(12));
        Assert.Equal(Enumerable.Range(2, 11), hits.Select(h => h.Target).Order());   // 10 passed, the 11th stops it, the 12th is untouched
        Assert.All(hits, h => Assert.Equal((100f, HitZone.Body, 1), (h.HealthDamage, h.Zone, h.Shooter)));
        Assert.Equal((ShotHit.Body, 12), (shot.Hit, shot.Target));   // the event names the player the shot stopped in
        Assert.InRange(shot.End.Y, 22f, 23f);

        // The stalker bolter: 10 points, 4 a body: passes 2, stops in the 3rd; Kraken Bolts: 2 points, 1 a body: the same.
        Assert.Equal([2, 3, 4], Line(Armed(Stalker), Level(), Row(5)).Hits.Select(h => h.Target).Order());
        Assert.Equal([2, 3, 4], Line(Armed(Kraken), Level(), Row(5)).Hits.Select(h => h.Target).Order());
        // Without penetration the first player stops the shot (as before M39).
        Assert.Equal([2], Line(Armed(null), Level(), Row(5)).Hits.Select(h => h.Target));
    }

    [Fact]
    public void Pierced_players_get_kill_credit_the_shield_and_health_split_and_their_head_zone()
    {
        // Shield 30, health 60: the 100 takes the shield and kills each of the three the stalker reaches (the record keeps the 70 dealt).
        var (world, shot, hits) = Line(Armed(Stalker, shield: 30f, health: 60f), Level(), Row(4));
        Assert.Equal((4, true), (shot.Target, shot.Armoured));   // the event's victim (the third) still had its shield up
        Assert.Equal([2, 3, 4], hits.Select(h => h.Target).Order());
        Assert.All(hits, h => Assert.Equal((30f, 70f, true), (h.ShieldDamage, h.HealthDamage, h.Killed)));
        Assert.Equal((false, false, false, true), (world.Players[2].Alive, world.Players[3].Alive, world.Players[4].Alive, world.Players[5].Alive));
        Assert.Equal(3, world.Scores[1].Kills);

        // Heads cost the stalker 3: level with the heads it passes three and stops in the fourth, at the head multiplier.
        var c = Armed(Stalker, head: 2.5f) with { EyeHeight = new SimConfig().HeadHeight };   // a level shot at head height
        var heads = Line(c, Level(), Row(6)).Hits;
        Assert.Equal([2, 3, 4, 5], heads.Select(h => h.Target).Order());
        Assert.All(heads, h => Assert.Equal((HitZone.Head, 250f), (h.Zone, h.HealthDamage)));
    }

    [Fact]
    public void A_pierced_shot_hits_every_player_where_the_shooter_saw_them()
    {
        var c = Armed(Lascannon) with { MaxRewindTicks = 30 };
        var world = new World(c, Level(), [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 4, 0), MathF.PI), new SpawnPoint(new Vector3(0, 8, 0), MathF.PI)]);
        world.Spawn(1); world.Spawn(2); world.Spawn(3);
        uint seen = world.Tick;
        // Both targets walk off the line (sideways) for half a second; the shot is sent with the older view.
        for (int k = 0; k < 15; k++) world.Step(id => new PlayerInput(world.Tick, id == 1 ? 0f : 1f, 0, MathF.PI, InputButtons.None));
        Assert.True(MathF.Abs(world.Players[2].Position.X) > 1f && MathF.Abs(world.Players[3].Position.X) > 1f);
        world.Step(id => id == 1 ? new PlayerInput(world.Tick, 0, 0, 0f, InputButtons.Fire, 0f, seen).WithShotDirection(Vector3.UnitY)
            : new PlayerInput(world.Tick, 0, 0, MathF.PI, InputButtons.None));
        Assert.Equal([2, 3], world.LastHits.Select(h => h.Target).Order());
    }

    [Fact]
    public void Players_before_a_crate_take_the_shot_and_the_points_they_spend_are_gone_for_the_crates()
    {
        // Lascannon: two players (2 points), a crate (1), the exit cone reaches the player behind it.
        var (_, shot, hits) = Line(Armed(Lascannon), Level(Crate(8f)), [new(0, 3, 0), new(0, 5, 0), new(0, 12, 0)]);
        Assert.Equal([(2, 100f), (3, 100f), (4, 50f)], hits.OrderBy(h => h.Target).Select(h => (h.Target, h.HealthDamage)));
        Assert.Equal((ShotHit.Body, 4), (shot.Hit, shot.Target));
        // Stalker: one player costs 4 of 10; the crate's 5 is still covered (1 left): the player behind is reached.
        Assert.Equal([(2, 100f), (3, 20f)], Line(Armed(Stalker), Level(Crate(8f)), [new(0, 3, 0), new(0, 12, 0)]).Hits.OrderBy(h => h.Target).Select(h => (h.Target, h.HealthDamage)));
        // Kraken Bolts: two players spend both points; the crate stops the shot.
        var kraken = Line(Armed(Kraken), Level(Crate(8f)), [new(0, 3, 0), new(0, 5, 0), new(0, 12, 0)]);
        Assert.Equal([2, 3], kraken.Hits.Select(h => h.Target).Order());
        Assert.Equal((ShotHit.Body, 3), (kraken.Shot.Hit, kraken.Shot.Target));
    }

    [Fact]
    public void An_exit_hits_a_player_once_even_when_two_exits_reach_them()
    {
        // Two crates 1 m apart: both exits' cones (15 m) reach the player at 9 m; 50 once, not 100.
        var (_, _, hits) = Line(Armed(Lascannon), Level(Crate(3f, 1f, "a"), Crate(5f, 1f, "b")), [new(0, 9, 0)]);
        Assert.Equal(50f, Assert.Single(hits).HealthDamage);
    }

    [Fact]
    public void A_charged_stage_pierces_with_its_own_entry_and_spares_allies()
    {
        var charged = ChargeTests.LasShot with { Pierce = new PierceSpec(10f, 0f, ExitCone(25f, 1f, 50f), false) { BodyCost = 0f, HeadCost = 0f } };
        var las = ChargeTests.Las with { Charge = ChargeTests.Las.Charge! with { Stages = [ChargeTests.Las.Charge.Stages[0], ChargeTests.Las.Charge.Stages[1] with { Shot = charged }] } };
        var c = new SimConfig { HealthMax = 1e6f, ShieldMax = 0f, Weapons = [las], Classes = [new ClassLoadout("las", 0, -1, []) { Side = Team.SpaceMarines }, new ClassLoadout("chaos", -1, -1, []) { Side = Team.Chaos }] };
        // With rules a spawn avoids enemies: the Chaos players take the first points in turn, then the two Space Marines the free
        // points farthest from them (the origin, then 20 m).
        var world = new World(c, Level(Crate(10f), Crate(14f, name: "second")), [.. Row(3).Select(p => new SpawnPoint(p, MathF.PI)),
            new SpawnPoint(new Vector3(0, 22, 0), MathF.PI), new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 20, 0), MathF.PI)])
        { Rules = new MatchRules { TimeLimit = 0f, FriendlyFire = true } };
        world.Spawn(2, 1); world.Spawn(3, 1); world.Spawn(4, 1); world.Spawn(6, 1); world.Spawn(1, 0); world.Spawn(5, 0);
        Assert.Equal((Vector3.Zero, 20f, 22f, Team.SpaceMarines, Team.SpaceMarines),
            (world.Players[1].Position, world.Players[5].Position.Y, world.Players[6].Position.Y, world.Players[1].Team, world.Players[5].Team));
        var hits = new List<HitEvent>();
        for (int k = 0; k <= 45; k++)
        {
            var buttons = k < 45 ? InputButtons.Fire : InputButtons.None;
            world.Step(id => id == 1 ? new PlayerInput(world.Tick, 0, 0, 0f, buttons).WithShotDirection(Vector3.UnitY) : new PlayerInput(world.Tick, 0, 0, MathF.PI, InputButtons.None));
            hits.AddRange(world.LastHits);
        }
        // Cost 0: every player on the ray (200 each), both crates; past them the enemies-only exit spares the ally at 20 m, hits the enemy at 22 m.
        Assert.Equal([(2, 200f), (3, 200f), (4, 200f), (6, 50f)], hits.OrderBy(h => h.Target).Select(h => (h.Target, h.HealthDamage)));
    }

    [Fact]
    public void Kraken_Bolts_pierce_only_for_a_player_with_the_perk()
    {
        const string upgrade = "mp_perk_weap_bolter_kraken_rounds";
        var bolter = new SimConfig().Weapons[0] with { PerkPierces = [new UpgradePierce(upgrade, Kraken)] };
        Assert.Null(Pierces.Of(bolter, 0, _ => false));
        Assert.Same(Kraken, Pierces.Of(bolter, 0, u => u == upgrade));
        Assert.Same(Lascannon, Pierces.Of(bolter with { Pierce = Lascannon }, 0, _ => false));
        Assert.Equal((float.PositiveInfinity, 4f, 3f), (Pierces.PlayerCost(null, HitZone.Body), Pierces.PlayerCost(Stalker, HitZone.Body), Pierces.PlayerCost(Stalker, HitZone.Head)));

        // In the world: perk 0 is Kraken Bolts (its upgrade string is the one the bolter's entry requires), perk 1 another.
        var armed = Armed(null);
        var c = armed with
        {
            Weapons = [armed.Weapons[0] with { PerkPierces = [new UpgradePierce(upgrade, Kraken)] }],
            Perks = [new PerkSpec("bolter_kraken_rounds", upgrade, PerkKind.Weapon, 1), new PerkSpec("bolter_accuracy", "mp_perk_weap_bolter_accuracy", PerkKind.Weapon, 1)],
            Classes = [armed.Classes[0] with { PerkChoices = [0, 1] }],
        };
        Assert.NotEqual(armed.Hash(), (armed with { Weapons = c.Weapons }).Hash());
        foreach (int perk in new[] { -1, 1, 0 })
        {
            // Two players, a crate, a player behind it: with the perk the shot passes both (2 points), and the crate is not covered any more.
            var world = new World(c, Level(Crate(8f)), [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 3, 0), MathF.PI), new SpawnPoint(new Vector3(0, 5, 0), MathF.PI),
                new SpawnPoint(new Vector3(0, 12, 0), MathF.PI)]);
            world.Spawn(1, 0, perk1Choice: perk); world.Spawn(2); world.Spawn(3); world.Spawn(4);
            Assert.Equal(perk == 0, Pierces.HasUpgrade(c, world.Players[1], upgrade));
            world.Step(id => id == 1 ? new PlayerInput(world.Tick, 0, 0, 0f, InputButtons.Fire).WithShotDirection(Vector3.UnitY) : new PlayerInput(world.Tick, 0, 0, MathF.PI, InputButtons.None));
            Assert.Equal(perk == 0 ? [2, 3] : [2], world.LastHits.Select(h => h.Target).Order());
        }
        // Kraken Bolts through a crate alone: 1 point a crate, the exit cone (16, enemies and neutrals) reaches the player behind.
        var through = new World(c, Level(Crate(3f)), [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 8, 0), MathF.PI)]);
        through.Spawn(1, 0, perk1Choice: 0); through.Spawn(2);
        through.Step(id => id == 1 ? new PlayerInput(through.Tick, 0, 0, 0f, InputButtons.Fire).WithShotDirection(Vector3.UnitY) : new PlayerInput(through.Tick, 0, 0, MathF.PI, InputButtons.None));
        Assert.Equal(16f, Assert.Single(through.LastHits).HealthDamage);
    }

    // ---------------------------------------------------------------- spans

    [Fact]
    public void Touching_crates_are_one_span_and_its_end_is_the_only_exit_into_the_open()
    {
        var origin = new Vector3(0f, 0f, 1f);
        // Shared face at y = 4.2: two props, one span; the stalker passes both (5 + 5).
        var touching = Level(Crate(3f, name: "a"), Crate(4.2f, name: "b"));
        var t = touching.TraceShot(origin, Vector3.UnitY, 100f, Stalker);
        Assert.Equal([(0, 3f, 4.2f, false), (1, 4.2f, 5.4f, true)], t.Pierced.Select(p => (p.Prop, MathF.Round(p.Entry, 3), MathF.Round(p.Exit, 3), p.IntoOpen)));
        Assert.Null(t.Stop);
        // 1 mm apart is still one span; 5 mm apart are two.
        Assert.Single(Level(Crate(3f, name: "a"), Crate(4.201f, name: "b")).TraceShot(origin, Vector3.UnitY, 100f, Stalker).Pierced, p => p.IntoOpen);
        Assert.Equal(2, Level(Crate(3f, name: "a"), Crate(4.205f, name: "b")).TraceShot(origin, Vector3.UnitY, 100f, Stalker).Pierced.Count(p => p.IntoOpen));
        // A stalker with points for one crate stops at the second's entry, inside the span: no exit into the open.
        var one = touching.TraceShot(origin, Vector3.UnitY, 100f, Stalker with { Points = 5f });
        Assert.Equal((0, false), (one.Pierced.Single().Prop, one.Pierced.Single().IntoOpen));
        Assert.Equal((1, 4.2f), (touching.PropOfShotTriangle(one.Stop!.Value.Triangle), MathF.Round(one.Stop.Value.Distance, 3)));

        // In the world: the lascannon through two touching crates reaches the player behind them.
        Assert.Equal(50f, Assert.Single(Line(Armed(Lascannon), Level(Crate(3f, name: "a"), Crate(4.2f, name: "b")), [new(0, 9, 0)]).Hits).HealthDamage);
    }

    [Fact]
    public void A_ray_along_a_shared_edge_or_grazing_a_face_and_an_origin_inside_a_prop()
    {
        var crate = Level(Crate(3f));
        // Through the crate's vertical edge (x = 2, at the corner y = 3 → y = 4.2 along the side face): a graze, no entry.
        var graze = crate.TraceShot(new Vector3(2f, 0f, 1f), Vector3.UnitY, 100f, Lascannon);
        Assert.Empty(graze.Pierced);
        // Diagonally through the edge where two faces meet (front and top at y = 3, z = 2.5): enters there and leaves.
        var edge = crate.TraceShot(new Vector3(0f, 0f, 2.5f + 3f), Vector3.Normalize(new Vector3(0f, 1f, -1f)), 100f, Lascannon);
        Assert.True(edge.Pierced.Count == 1 && edge.Pierced[0].IntoOpen, $"{string.Join(", ", edge.Pierced)}");
        Assert.Equal(3f * MathF.Sqrt(2f), edge.Pierced[0].Entry, 3);
        // From inside: entered at 0, left at the far face.
        var inside = crate.TraceShot(new Vector3(0f, 3.5f, 1f), Vector3.UnitY, 100f, Lascannon);
        Assert.Equal((0, 0f, 0.7f, true), (inside.Pierced.Single().Prop, inside.Pierced.Single().Entry, MathF.Round(inside.Pierced.Single().Exit, 3), inside.Pierced.Single().IntoOpen));
        // From inside without penetration: the far face stops it.
        Assert.Equal(0.7f, crate.TraceShot(new Vector3(0f, 3.5f, 1f), Vector3.UnitY, 100f, null).Stop!.Value.Distance, 3);
        // A prop that runs into the level's wall: entered, cut at the wall, which stops the shot; no exit into the open.
        var wall = new StaticMesh([new(-80, -80, 0), new(80, -80, 0), new(80, 80, 0), new(-80, 80, 0), new(-20, 4, 0), new(20, 4, 0), new(20, 4, 9), new(-20, 4, 9)],
            [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]);
        var cut = new CollisionWorld(wall, wall, props: [Crate(3f)]).TraceShot(new Vector3(0f, 0f, 1f), Vector3.UnitY, 100f, Lascannon);
        Assert.Equal((4f, false), (MathF.Round(cut.Pierced.Single().Exit, 3), cut.Pierced.Single().IntoOpen));
        Assert.Equal(-1, new CollisionWorld(wall, wall, props: [Crate(3f)]).PropOfShotTriangle(cut.Stop!.Value.Triangle));
    }

    // ---------------------------------------------------------------- what else a crate stops

    [Fact]
    public void A_crate_stops_melee_and_the_melta_cone()
    {
        foreach (bool crate in new[] { false, true })
        {
            var level = crate ? Level(PropTests.Box("low wall", new Vector3(-3f, 0.7f, 0f), new Vector3(3f, 1.3f, 2.5f))) : Level();
            var c = new SimConfig { HealthMax = 1e6f, ShieldMax = 0f };
            var world = new World(c, level, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 2, 0), MathF.PI)]);
            world.Spawn(1); world.Spawn(2);
            var hits = new List<HitEvent>();
            for (int k = 0; k < 40; k++)
            {
                world.Step(id => new PlayerInput(world.Tick, 0, 0, id == 1 ? 0f : MathF.PI, id == 1 && k == 0 ? InputButtons.Melee : InputButtons.None));
                hits.AddRange(world.LastHits.Where(h => h.Melee));
            }
            Assert.Equal(crate ? 0 : 1, hits.Count);
        }

        foreach (bool crate in new[] { false, true })
        {
            var c = new SimConfig { HealthMax = 1e6f, ShieldMax = 0f, Weapons = [MeltaTests.Melta], Classes = [new ClassLoadout("melta", 0, -1, []), new ClassLoadout("target", -1, -1, [])] };
            var world = new World(c, crate ? Level(Crate(2f)) : Level(), [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 5, 0), MathF.PI)]);
            world.Spawn(1, 0); world.Spawn(2, 1);
            float pitch = MathF.Atan2((c.BodyBottom + c.BodyTop) / 2f - c.EyeHeight, 5f);
            world.Step(id => new PlayerInput(world.Tick, 0, 0, id == 1 ? 0f : MathF.PI, id == 1 ? InputButtons.Fire : InputButtons.None, id == 1 ? pitch : 0f));
            Assert.Equal(crate ? 0 : 1, world.LastHits.Count(h => h.Cone));
            if (crate) Assert.Equal((ShotHit.World, 4), (world.LastShots.Single().Hit, world.LastShots.Single().Surface));
        }
    }
}
