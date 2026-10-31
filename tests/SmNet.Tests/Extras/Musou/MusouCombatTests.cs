using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou" (MU3): the players' attacks on the enemies, on the flat ground. A hit-scan shot's tracer ends on the enemy it hit; a cone hits
/// several; a projectile that detonates on contact goes off on the enemy it meets; a frag grenade's blast kills a pack, staggers and knocks back a survivor
/// and leaves one outside its radius alone; the cleave hits at most 8, nearest first, only in its cone; a heavy step staggers and knocks back (a light one
/// does not); a charge hits the enemies on its path; and every KO is the attacker's.
/// </summary>
public class MusouCombatTests
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 2000, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    static readonly WeaponSpec Flamer = Rifle with
    {
        Name = "flamer", Delivery = DeliveryKind.Cone,
        Cone = new ConeSpec(8f, 40f, 0.5f, 0f, Plain with { Damage = 30f }, [new(0f, 1f), new(8f, 1f)], []),
    };
    static readonly ProjectileSpec Rocket = new("ebps\\test\\rocket", 40f, 5f, "raycast", 0f, [new(0f, 0f)], true, true, false, 0, 0f, 0f, 0, Plain with { Damage = 50f },
        new SplashSpec(3f, Plain with { Damage = 100f }, [new(0f, 1f), new(3f, 1f)], false, false, "target_all"));
    static readonly WeaponSpec Launcher = Rifle with { Name = "launcher", Delivery = DeliveryKind.Projectile, Projectile = Rocket, ShotCooldown = 1f };

    static readonly RigidSpec Rigid = new(65f, 0.3f, 0.4f, 0.05f, 0.05f, [new(0f, 1f), new(90f, 0.2f)], [new(0f, 1f), new(90f, 0.2f)], 0.1f, 0.1f);
    static readonly DistancePoint[] FragFallOff = [new(0f, 1f), new(2.5f, 1f), new(2.6f, 0.5f), new(5f, 0.5f), new(5.1f, 0.3f), new(7f, 0.3f), new(10f, 0.05f)];
    static readonly ProjectileSpec FragBall = new("ebps\\test\\frag", 20f, 30f, "rigidbody", 0f, [new(5f, 0f), new(10f, 1f)], false, true, false, 1, 1f, 1.5f, 0, null,
        new SplashSpec(6f, new DamageSpec(120f, 0f, 1f, 1f, 1f, 1f, false), FragFallOff, true, false, "target_all")) { Rigid = Rigid, StopAtMaxBounce = true };
    static readonly ThrownSpec Frag = new("frag", 3, 3, 1f, 10f, FragBall);

    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static readonly MeleeStep Heavy = new(1.2f, 0.3f, 0f, 0f, 0.8f, 0.9f, 45f, 10f, 3.5f) { PrimaryReaction = new(0f, ReactionKind.Stun) };
    static Vector2[] Line(float metres, int samples) => [.. Enumerable.Range(0, samples + 1).Select(k => new Vector2(0f, metres * k / samples))];
    static readonly RushSpec Charge = new("bullrush", new RushMove("rush", Line(12f, 12), 1.2f, 1.0f, 0.6f, 0.5f, 1.0f), new RushMove("rush_stop", Line(0.5f, 10), 1.0f, 0.9f, 0.9f, 0.6f, 0.3f),
        Plain with { Damage = 40f }, new ReactionEntry(0f, ReactionKind.Stun), Plain with { Damage = 30f }, new ReactionEntry(0f, ReactionKind.Interrupt), 4f, 1f)
        { ContactUntil = 0.6f, WallFrom = 0.3f, HyperArmourUntil = 0.3f };

    /// <summary>Boyz that stand still (speed 0), so a test can place them.</summary>
    static readonly MusouRules Still = MusouRules.Default with
    {
        PlayerDamageScale = 0f,
        Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })],
    };

    static World NewWorld(int primary = 0)
    {
        var cls = new ClassLoadout("sm - tactical", primary, -1, [Light]) { PrimaryChoices = [primary], EquipmentChoices = [0], MeleeHeavy = [Heavy], MeleeRush = Charge, Side = Team.SpaceMarines, Role = "tactical" };
        var config = MusouSimTests.Base with
        {
            Weapons = [Rifle, Flamer, Launcher], Thrown = [Frag],
            Classes = [cls, cls with { Name = "csm - tactical", Side = Team.Chaos }],
            MusouRules = Still,
        };
        var w = new World(config) { Rules = MusouSimTests.Endless };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, 0));   // in play, facing +y
        Assert.Equal(MatchState.Playing, w.State);
        return w;
    }

    static Vector3 Me(World w) => w.Players[1].Position;

    /// <summary>Steps with <paramref name="input"/> on the first tick and none after, collecting the Musou events.</summary>
    static List<MusouEvent> Run(World w, int ticks, InputButtons press, float moveY = 0f)
    {
        var events = new List<MusouEvent>();
        for (int t = 0; t < ticks; t++)
        {
            w.Step(_ => new PlayerInput(0, 0f, t == 0 ? moveY : 0f, 0f, t == 0 ? press : 0));
            events.AddRange(w.Musou!.Events);
        }
        return events;
    }

    [Fact]
    public void A_shots_tracer_ends_on_the_enemy_it_hit()
    {
        var w = NewWorld();
        var sim = w.Musou!;
        // Nothing in the way: the tracer runs to the range.
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire));
        var free = Assert.Single(w.LastShots);
        Assert.Equal(ShotHit.None, free.Hit);
        Assert.InRange(Vector3.Distance(free.Origin, free.End), 249f, 251f);

        ushort boy = sim.Spawn(MusouEnemyType.Boy, Me(w) + new Vector3(0f, 12f, 0f), 0f, 85f);
        ShotEvent shot = default;
        for (int t = 0; t < 30 && shot.Hit == ShotHit.None; t++)
        {
            w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire));
            if (w.LastShots.Count > 0) shot = w.LastShots[0];
        }
        Assert.True(shot.Hit is ShotHit.Body or ShotHit.Head);
        Assert.Equal(0, shot.Target);
        Assert.True(sim.TryGet(boy, out var e));
        Assert.True(e.Health < 85f);
        // The end lies on the boy's near surface: its body cylinder (0.4 m off its axis) or its head sphere (0.22 m from its centre at 1.68 m).
        float off = shot.Hit == ShotHit.Head
            ? Vector3.Distance(shot.End, e.Position + new Vector3(0f, 0f, 1.68f))
            : new Vector2(shot.End.X - e.Position.X, shot.End.Y - e.Position.Y).Length();
        Assert.Equal(shot.Hit == ShotHit.Head ? 0.22f : 0.4f, off, 2);
        Assert.True(shot.End.Y < e.Position.Y);
    }

    [Fact]
    public void A_cone_hits_several_enemies_in_front_and_none_behind()
    {
        var w = NewWorld(primary: 1);
        var sim = w.Musou!;
        var me = Me(w);
        ushort[] front = [.. new[] { -1.2f, -0.4f, 0.4f, 1.2f }.Select(x => sim.Spawn(MusouEnemyType.Boy, me + new Vector3(x, 4f, 0f), 0f, 85f))];
        ushort behind = sim.Spawn(MusouEnemyType.Boy, me + new Vector3(0f, -3f, 0f), 0f, 85f);
        ushort far = sim.Spawn(MusouEnemyType.Boy, me + new Vector3(0f, 10f, 0f), 0f, 85f);
        var events = new List<MusouEvent>();
        ShotEvent shot = default;
        for (int t = 0; t < 5 && shot.Weapon == 0 && shot.Tick == 0; t++)
        {
            w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire));
            events.AddRange(sim.Events);
            if (w.LastShots.Count > 0) shot = w.LastShots[0];
        }
        Assert.Equal(1, shot.Weapon);
        Assert.Equal(ShotHit.Body, shot.Hit);
        var hit = events.Where(e => e.Kind == MusouEventKind.Hit).Select(e => e.Enemy).ToHashSet();
        Assert.True(hit.Count(front.Contains) >= 3, $"cone hit {hit.Count(front.Contains)} of the 4 in front");
        Assert.DoesNotContain(behind, hit);
        Assert.DoesNotContain(far, hit);   // past the cone's 8 m
    }

    [Fact]
    public void A_projectile_goes_off_on_the_enemy_it_meets()
    {
        var w = NewWorld(primary: 2);
        var sim = w.Musou!;
        var me = Me(w);
        ushort target = sim.Spawn(MusouEnemyType.Boy, me + new Vector3(0f, 10f, 0f), 0f, 85f);
        ushort beside = sim.Spawn(MusouEnemyType.Boy, me + new Vector3(1.2f, 10.5f, 0f), 0f, 85f);
        DetonationEvent? blast = null;
        for (int t = 0; t < 30 && blast is null; t++)
        {
            w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, t == 0 ? InputButtons.Fire : 0));
            if (w.LastDetonations.Count > 0) blast = w.LastDetonations[0];
        }
        Assert.NotNull(blast);
        Assert.InRange(blast.Value.Position.Y - me.Y, 9f, 10f);   // on the boy, not on the ground beyond
        Assert.True(sim.TryGet(target, out var a) && !a.Alive);
        Assert.True(sim.TryGet(beside, out var b) && !b.Alive);   // the splash
        Assert.Equal(2, w.Scores[1].Kills);
    }

    [Fact]
    public void A_frag_grenade_blast_kills_a_pack_staggers_a_survivor_and_spares_one_outside()
    {
        var w = NewWorld();
        var sim = w.Musou!;
        ProjectileState? landed = null;
        for (int t = 0; t < 120 && landed is null; t++)
        {
            w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, t == 0 ? InputButtons.Grenade : 0));
            if (w.Projectiles.Count > 0 && w.Projectiles[0] is { Motion: ProjectileMotion.Stuck } p) landed = p;
        }
        Assert.NotNull(landed);
        var at = landed.Value.Position with { Z = 0f };
        Assert.True(Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(Me(w).X, Me(w).Y)) > 8f);   // away from the thrower
        var pack = new List<ushort>();
        for (int k = 0; k < 6; k++)
        {
            float a = k * MathF.Tau / 6f;
            pack.Add(sim.Spawn(MusouEnemyType.Boy, at + new Vector3(MathF.Cos(a), MathF.Sin(a), 0f) * 1.5f, 0f, 85f));
        }
        var survivorAt = at + new Vector3(4f, 0f, 0f);
        ushort survivor = sim.Spawn(MusouEnemyType.Boy, survivorAt, 0f, 85f);
        ushort outside = sim.Spawn(MusouEnemyType.Boy, at + new Vector3(-6.5f, 0f, 0f), 0f, 85f);
        var events = new List<MusouEvent>();
        for (int t = 0; t < 60 && w.Projectiles.Count > 0; t++) { w.Step(_ => default); events.AddRange(sim.Events); }
        Assert.Empty(w.Projectiles);
        Assert.All(pack, id => Assert.True(sim.TryGet(id, out var e) && !e.Alive));
        Assert.All(events.Where(e => e.Kind == MusouEventKind.Death), e => Assert.Equal(((ushort)1, (byte)MusouDeathKind.Blast), (e.Player, e.Value)));
        Assert.Equal(6, w.Scores[1].Kills);
        Assert.Equal(6, w.MusouDirector!.Scores[1].Kos);
        // The survivor: half the damage at 4 m, staggered, knocked away from the blast (half the 2 m).
        Assert.True(sim.TryGet(survivor, out var s));
        Assert.Equal(85f - 60f, s.Health, 2);
        Assert.Equal(MusouEnemyState.Stagger, s.State);
        for (int t = 0; t < 10; t++) w.Step(_ => default);
        Assert.True(sim.TryGet(survivor, out s));
        Assert.InRange(s.Position.X - survivorAt.X, 0.9f, 1.1f);
        Assert.True(sim.TryGet(outside, out var o));
        Assert.Equal(85f, o.Health);
    }

    [Fact]
    public void The_cleave_hits_at_most_eight_nearest_first_and_only_in_its_cone()
    {
        var w = NewWorld();
        var sim = w.Musou!;
        var me = Me(w);
        // Ten in front (within 4.5 m and 90 degrees of the facing), nearest first; two behind; one too far.
        var front = new List<ushort>();
        for (int k = 0; k < 10; k++)
        {
            float distance = 1.2f + k * 0.3f, angle = (k % 2 == 0 ? 1f : -1f) * (10f + 7f * k) * MathF.PI / 180f;
            front.Add(sim.Spawn(MusouEnemyType.Boy, me + new Vector3(-MathF.Sin(angle), MathF.Cos(angle), 0f) * distance, 0f, 85f));
        }
        ushort[] behind = [sim.Spawn(MusouEnemyType.Boy, me + new Vector3(-1f, -1.5f, 0f), 0f, 85f), sim.Spawn(MusouEnemyType.Boy, me + new Vector3(1f, -1.5f, 0f), 0f, 85f)];
        ushort far = sim.Spawn(MusouEnemyType.Boy, me + new Vector3(0f, 4.9f, 0f), 0f, 85f);
        var events = Run(w, 20, InputButtons.Melee);
        var hits = events.Where(e => e.Kind == MusouEventKind.Hit).ToList();
        Assert.Equal(8, hits.Count);
        Assert.All(hits, h => Assert.Equal(((ushort)1, (byte)3), (h.Player, h.Value)));
        Assert.Equal(front.Take(8).ToHashSet(), hits.Select(h => h.Enemy).ToHashSet());
        foreach (var id in front.Take(8)) { Assert.True(sim.TryGet(id, out var e)); Assert.Equal(85f - 25f, e.Health); }
        foreach (var id in front.Skip(8).Concat(behind).Append(far)) { Assert.True(sim.TryGet(id, out var e)); Assert.Equal(85f, e.Health); }
        // A light step staggers nobody.
        Assert.All(front, id => Assert.True(sim.TryGet(id, out var e) && e.State != MusouEnemyState.Stagger));
    }

    [Fact]
    public void A_heavy_step_staggers_and_knocks_back_and_a_kill_is_the_attackers()
    {
        var w = NewWorld();
        var sim = w.Musou!;
        var me = Me(w);
        var start = me + new Vector3(0f, 2f, 0f);
        ushort boy = sim.Spawn(MusouEnemyType.Boy, start, 0f, 85f);
        ushort frail = sim.Spawn(MusouEnemyType.Boy, me + new Vector3(1.5f, 1.5f, 0f), 0f, 30f);
        var events = new List<MusouEvent>();
        bool staggered = false;
        for (int t = 0; t < 30; t++)
        {
            w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, t == 0 ? InputButtons.MeleeHeavy : 0));
            events.AddRange(sim.Events);
            staggered |= sim.TryGet(boy, out var b) && b.State == MusouEnemyState.Stagger;
        }
        Assert.True(staggered);
        Assert.True(sim.TryGet(boy, out var e));
        Assert.Equal(85f - 45f, e.Health);
        Assert.InRange(e.Position.Y - start.Y, 1.9f, 2.1f);   // 2 m straight back
        Assert.Equal(start.X, e.Position.X, 3);
        var death = Assert.Single(events, v => v.Kind == MusouEventKind.Death);
        Assert.Equal((frail, (ushort)1, (byte)MusouDeathKind.Melee), (death.Enemy, death.Player, death.Value));
        Assert.Equal(1, w.Scores[1].Kills);
        Assert.Equal(10, w.MusouDirector!.Scores[1].Points);
    }

    [Fact]
    public void A_charge_hits_the_enemies_on_its_path_once_each()
    {
        var w = NewWorld();
        var sim = w.Musou!;
        var me = Me(w);
        ushort[] path = [sim.Spawn(MusouEnemyType.Boy, me + new Vector3(0.3f, 3f, 0f), 0f, 85f), sim.Spawn(MusouEnemyType.Boy, me + new Vector3(-0.3f, 6f, 0f), 0f, 85f)];
        ushort aside = sim.Spawn(MusouEnemyType.Boy, me + new Vector3(4f, 4f, 0f), 0f, 85f);
        var events = new List<MusouEvent>();
        for (int t = 0; t < 45; t++)
        {
            bool held = t < 2;
            w.Step(_ => new PlayerInput(0, 0f, held ? 1f : 0f, 0f, held ? InputButtons.Sprint | (t == 1 ? InputButtons.MeleeHeavy : 0) : 0));
            events.AddRange(sim.Events);
        }
        var hits = events.Where(e => e.Kind == MusouEventKind.Hit).ToList();
        Assert.Equal(path.ToHashSet(), hits.Select(h => h.Enemy).ToHashSet());
        Assert.Equal(2, hits.Count);
        foreach (var id in path) { Assert.True(sim.TryGet(id, out var e)); Assert.Equal(85f - 40f, e.Health); }
        Assert.True(sim.TryGet(aside, out var a));
        Assert.Equal(85f, a.Health);
    }
}
