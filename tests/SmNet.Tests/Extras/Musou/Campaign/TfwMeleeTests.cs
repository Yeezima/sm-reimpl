using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Musou Mode's melee and firepower (pass 2, M5; items 7, 21, 22, 24; <see cref="TfwCombatRules"/>), in a war on the synthetic arena: a Marine moving into fodder throws it down, elites never fall, the
/// fall ends and stands up, sometimes kills, and a Guardsman takes the same path behind the switch; one throw is three big frags for one grenade; a swing reaches 20; shots and projectiles go through
/// by the weapon's weight. The 5-wave run and PvP keep all of it out (<see cref="MusouSkirmishPinTests"/>, <c>PvpCombatIsolationTests</c>; here the 5-wave run's own side of the switch).
/// </summary>
public class TfwMeleeTests
{
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    static readonly WeaponSpec Rifle = new("sm_mp_ranged_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.12f, 30, 2000, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly ProjectileSpec Rocket = new("ebps\\test\\rocket", 40f, 5f, "raycast", 0f, [new(0f, 0f)], true, true, false, 0, 0f, 0f, 0, Plain with { Damage = 1f },
        new SplashSpec(1.5f, Plain with { Damage = 1f }, [new(0f, 1f), new(1.5f, 1f)], false, false, "target_all"));
    static readonly WeaponSpec Launcher = Rifle with { Name = "sm_mp_ranged_grenade_launcher", Delivery = DeliveryKind.Projectile, Projectile = Rocket, ShotCooldown = 1f };

    static readonly RigidSpec Rigid = new(65f, 0.3f, 0.4f, 0.05f, 0.05f, [new(0f, 1f), new(90f, 0.2f)], [new(0f, 1f), new(90f, 0.2f)], 0.1f, 0.1f);
    static readonly DistancePoint[] FragFallOff = [new(0f, 1f), new(2.5f, 1f), new(5f, 0.5f), new(7f, 0.3f), new(10f, 0.05f)];
    static readonly ProjectileSpec FragBall = new("ebps\\test\\frag", 20f, 30f, "rigidbody", 0f, [new(5f, 0f), new(10f, 1f)], false, true, false, 1, 1f, 1.5f, 0, null,
        new SplashSpec(10f, new DamageSpec(200f, 0f, 1f, 1f, 1f, 1f, false), FragFallOff, false, false, "target_all")) { Rigid = Rigid, StopAtMaxBounce = true };
    static readonly ThrownSpec Frag = new("sm_mp_eq_frag_grenade", 2, 1, 1f, 10f, FragBall);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 1f, 10f, 3.5f);
    static readonly MeleeStep Heavy = new(1.2f, 0.3f, 0f, 0f, 0.8f, 0.9f, 1f, 10f, 3.5f) { PrimaryReaction = new(0f, ReactionKind.Stun) };

    static readonly MusouRules Still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };

    static SimConfig Config(int primary = 0)
    {
        var c = new ClassLoadout("sm - tactical", primary, -1, [Light]) { PrimaryChoices = [primary], EquipmentChoices = [0], MeleeHeavy = [Heavy], Side = Team.SpaceMarines, Role = "tactical" };
        return MusouSimTests.Base with
        {
            Weapons = [Rifle, Launcher], Thrown = [Frag], Classes = [c, c with { Name = "csm - tactical", Side = Team.Chaos }], Extras = ContentExtras.Musou, MusouRules = Still, RespawnTime = 60f,
        };
    }

    const int Human = 1;
    static readonly Vector3 At = new(0f, -60f, 0f);

    static World War(int primary = 0)
    {
        var map = TfwTestArena.Build();
        var w = new World(Config(primary), map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;   // no run: these tests place everybody themselves
        w.Spawn(Human, 0);
        Tick(w);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(Human, At, 0f);
        Tick(w);
        return w;
    }

    static void Tick(World w, float moveY = 0f, InputButtons buttons = InputButtons.None) =>
        w.Step(id => new PlayerInput(w.Tick, 0f, moveY, w.Players[id].Yaw, buttons));

    static ushort Boy(World w, Vector3 at, MusouEnemyType type = MusouEnemyType.Boy, float health = 1000f)
    {
        ushort id = w.Musou!.Spawn(type, at, 0f, health);
        Assert.NotEqual(0, id);
        return id;
    }

    static MusouEnemyState State(World w, ushort id)
    {
        Assert.True(w.Musou!.TryGet(id, out var e));
        return e.State;
    }

    // ---------------------------------------------------------------- the fall (item 7)

    [Theory]
    [InlineData(MusouEnemyType.Boy, true)]
    [InlineData(MusouEnemyType.ShootaBoy, true)]
    [InlineData(MusouEnemyType.Gretchin, true)]
    [InlineData(MusouEnemyType.ArdBoy, false)]
    [InlineData(MusouEnemyType.Nob, false)]
    [InlineData(MusouEnemyType.ChaosMarine, false)]
    [InlineData(MusouEnemyType.Warboss, false)]
    public void Only_fodder_falls(MusouEnemyType type, bool falls)
    {
        var sim = new MusouSim(null) { War = TfwCombatRules.Default };
        ushort id = sim.Spawn(type, Vector3.Zero, 0f, 100f);
        Assert.Equal(falls, sim.KnockDown(id, Vector2.UnitY, 2f, 1, 0f));
        sim.TryGet(id, out var e);
        Assert.Equal(falls, e.State == MusouEnemyState.Knocked);
    }

    [Fact]
    public void Nothing_falls_outside_a_war()
    {
        var sim = new MusouSim(null);
        ushort id = sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, 100f);
        Assert.False(sim.KnockDown(id, Vector2.UnitY, 2f, 1, 1f));
        sim.TryGet(id, out var e);
        Assert.Equal(MusouEnemyState.Spawning, e.State);
    }

    [Fact]
    public void A_fallen_Ork_is_thrown_lies_still_strikes_nobody_and_stands_after_the_fall()
    {
        var rules = MusouRules.Default with { PlayerDamageScale = 1f };
        var sim = new MusouSim(null, rules) { War = TfwCombatRules.Default };
        ushort id = sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, 100f);
        for (int k = 0; k < 30; k++) { sim.BeginTick((uint)k + 1); sim.Step([], null, true, []); }
        sim.BeginTick(100);
        Assert.True(sim.KnockDown(id, Vector2.UnitY, 3f, 1, 0f));
        Assert.False(sim.KnockDown(id, Vector2.UnitY, 3f, 1, 1f));   // he cannot fall twice

        MusouTarget[] target = [new(1, new Vector3(0f, 6f, 0f), true)];
        var attacks = new List<MusouAttack>();
        int ticks = (int)(TfwCombatRules.Default.KnockedSeconds * sim.TickRate);
        MusouEnemy e = default;
        for (int k = 0; k < ticks - 1; k++)
        {
            sim.BeginTick(101 + (uint)k);
            sim.Step(target, null, true, attacks);
            sim.TryGet(id, out e);
            Assert.Equal(MusouEnemyState.Knocked, e.State);
        }
        Assert.Empty(attacks);
        Assert.InRange(e.Position.Y, 2.5f, 3.5f);   // the throw, and no step of his own after it
        sim.BeginTick(500); sim.Step(target, null, true, attacks);
        sim.BeginTick(501); sim.Step(target, null, true, attacks);
        sim.TryGet(id, out e);
        Assert.NotEqual(MusouEnemyState.Knocked, e.State);
    }

    [Fact]
    public void Sometimes_the_fall_kills_and_the_kill_is_the_Marines()
    {
        var sim = new MusouSim(null) { War = TfwCombatRules.Default };
        ushort id = sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, 100f);
        Assert.True(sim.KnockDown(id, Vector2.UnitY, 3f, 7, 1f));
        sim.TryGet(id, out var e);
        Assert.Equal(MusouEnemyState.Dying, e.State);
        var kills = new List<MusouKill>();
        sim.TakeKills(kills);
        Assert.Equal(7, Assert.Single(kills).Player);
    }

    [Fact]
    public void The_death_roll_is_about_its_chance()
    {
        var sim = new MusouSim(null) { War = TfwCombatRules.Default };
        int died = 0;
        for (int n = 0; n < 400; n++)
        {
            sim.BeginTick((uint)n * 3 + 1);
            ushort id = sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, 100f);
            sim.KnockDown(id, Vector2.UnitY, 1f, 1, TfwCombatRules.Default.SprintDeath);
            sim.TryGet(id, out var e);
            if (e.State == MusouEnemyState.Dying) died++;
            sim.Remove(id);
        }
        Assert.InRange(died, 20, 65);   // 10 % of 400
    }

    [Fact]
    public void A_Marine_walking_into_fodder_throws_it_down_and_an_elite_only_stands_in_his_way()
    {
        var w = War();
        w.Musou!.Frozen = true;
        var me = w.Players[Human].Position;
        ushort boy = Boy(w, me + new Vector3(0.5f, 1f, 0f)), nob = Boy(w, me + new Vector3(-0.5f, 1f, 0f), MusouEnemyType.Nob), behind = Boy(w, me + new Vector3(0f, -1.0f, 0f));
        for (int k = 0; k < 6; k++) Tick(w, moveY: 1f);
        Assert.Equal(MusouEnemyState.Knocked, State(w, boy));
        Assert.Equal(MusouEnemyState.Spawning, State(w, nob));
        Assert.Equal(MusouEnemyState.Spawning, State(w, behind));   // he moved away from this one
    }

    [Fact]
    public void Standing_still_shoves_nobody()
    {
        var w = War();
        w.Musou!.Frozen = true;
        ushort boy = Boy(w, w.Players[Human].Position + new Vector3(0f, 0.8f, 0f));
        for (int k = 0; k < 10; k++) Tick(w);
        Assert.Equal(MusouEnemyState.Spawning, State(w, boy));
    }

    [Fact]
    public void A_shoved_Guardsman_falls_stops_firing_stands_again_or_is_lost_without_credit()
    {
        var sim = new MusouSim(null) { Frozen = true };
        var guards = new GuardSim(sim);
        Assert.Equal(0, guards.SpawnSquad(Vector3.Zero));
        guards.BeginTick(1); guards.Step(true);
        var rules = TfwCombatRules.Default;
        Assert.Equal(1, guards.Shove(new Vector3(-3f, -0.75f, 0f), 0.2f + 1.5f, default, rules.WalkPush, 0f, rules) is > 0 ? 1 : 0);
        var men = new TfwGuard[GuardSim.Capacity];
        int n = guards.CopyTo(men);
        var down = men.Take(n).Where(m => m.State == (byte)GuardState.Knocked).ToArray();
        Assert.NotEmpty(down);
        var before = down[0].Position;
        for (uint t = 2; t < 2 + (uint)(rules.KnockedSeconds * sim.TickRate) - 2; t++) { guards.BeginTick(t); guards.Step(true); }
        guards.CopyTo(men);
        Assert.Equal((byte)GuardState.Knocked, men.First(m => m.Id == down[0].Id).State);
        Assert.NotEqual(before, men.First(m => m.Id == down[0].Id).Position);   // the throw
        for (uint t = 200; t < 206; t++) { guards.BeginTick(t); guards.Step(true); }
        guards.CopyTo(men);
        Assert.NotEqual((byte)GuardState.Knocked, men.First(m => m.Id == down[0].Id).State);

        int alive = guards.Alive;
        guards.BeginTick(300);
        int thrown = guards.Shove(guards.Centre(0), 20f, default, rules.ChargePush, 1f, rules);
        Assert.True(thrown > 0);
        Assert.Equal(alive - thrown, guards.Alive);
        Assert.Equal(thrown, guards.Lost);
        Assert.Equal(thrown, guards.Events.Count(e => e.Kind == GuardEventKind.Death));
    }

    // ---------------------------------------------------------------- the frag (item 21)

    static void Throw(World w)
    {
        Tick(w, buttons: InputButtons.Grenade);
        for (int k = 0; k < 60 && w.Projectiles.Count == 0; k++) Tick(w);
    }

    [Fact]
    public void One_throw_is_three_frags_in_a_fan_for_one_grenade()
    {
        var w = War();
        int carried = w.Players[Human].Grenades;
        Assert.True(carried > 0);
        Throw(w);
        Assert.Equal(3, w.Projectiles.Count);
        Assert.Equal(carried - 1, w.Players[Human].Grenades);
        Assert.Equal(3, w.Projectiles.Select(p => p.Id).Distinct().Count());
        var headings = w.Projectiles.Select(p => MathF.Atan2(p.Velocity.X, p.Velocity.Y) * 180f / MathF.PI).OrderBy(a => a).ToArray();
        Assert.Equal(TfwCombatRules.Default.FragSpreadDegrees, headings[1] - headings[0], 1f);
        Assert.Equal(TfwCombatRules.Default.FragSpreadDegrees, headings[2] - headings[1], 1f);
        Assert.All(w.Projectiles, p => Assert.Equal(Frag.Projectile.Splash!.Radius * 2f, p.Spec!.Splash!.Radius));
    }

    [Fact]
    public void A_frags_blast_reaches_twice_as_far_tells_the_clients_and_spares_the_thrower()
    {
        var w = War();
        w.Musou!.Frozen = true;
        Throw(w);
        var fuse = w.Projectiles[0];
        ushort far = 0;
        int blasts = 0;
        float health = w.Players[Human].Health + w.Players[Human].Shield;
        for (int k = 0; k < 300 && w.Projectiles.Count > 0; k++)
        {
            // 14 m from where it will land: outside the data's 10 m, inside the 20.
            if (far == 0 && w.Projectiles[0].Position.Z < 1.5f) far = Boy(w, w.Projectiles[0].Position with { Z = 0f } + new Vector3(14f, 0f, 0f), health: 10000f);
            Tick(w);
            blasts += w.LastDetonations.Count(d => d.Thrown);
        }
        Assert.Equal(3, blasts);
        Assert.True(w.Musou.TryGet(far, out var e) && e.Health < 10000f, "the blast reached past the data's radius");
        Assert.Equal(health, w.Players[Human].Health + w.Players[Human].Shield);
        Assert.Equal(3, w.Tfw!.Smoke.Count(f => f.Kind == TfwFireKind.Frag));
        Assert.All(w.Tfw.Smoke, f => Assert.Equal(20f, f.To.Y));
    }

    [Fact]
    public void A_later_life_carries_grenades_again()
    {
        var w = War();
        Throw(w);
        int left = w.Players[Human].Grenades;
        w.TfwRespawnAt(Human, At, 0f);
        Tick(w);
        Assert.True(w.Players[Human].Grenades >= left);
    }

    // ---------------------------------------------------------------- the cleave (item 22)

    [Theory]
    [InlineData(InputButtons.Melee)]
    [InlineData(InputButtons.MeleeHeavy)]
    public void A_swing_reaches_twenty_enemies_and_draws_each_a_slash(InputButtons press)
    {
        var w = War();
        w.Musou!.Frozen = true;
        var me = w.Players[Human].Position;
        for (int k = 0; k < 30; k++)
        {
            float angle = (k - 14.5f) * 0.06f, ring = 1.6f + k % 3 * 0.25f;
            Boy(w, me + new Vector3(MathF.Sin(angle) * ring, MathF.Cos(angle) * ring, 0f), k % 5 == 0 ? MusouEnemyType.ArdBoy : MusouEnemyType.Boy);
        }
        int slashes = 0;
        for (int t = 0; t < 40; t++)
        {
            Tick(w, buttons: t < 3 ? press : InputButtons.None);
            slashes = Math.Max(slashes, w.Musou.Events.Count(e => e.Kind == MusouEventKind.Hit && e.Value == 3));
        }
        Assert.Equal(TfwCombatRules.Default.CleaveTargets, slashes);
        Assert.Equal(20, w.TfwBestCleave);
    }

    [Fact]
    public void A_melee_hit_throws_fodder_down_and_an_elite_staggers_only_at_a_heavy_blow()
    {
        var w = War();
        w.Musou!.Frozen = true;
        var me = w.Players[Human].Position;
        ushort boy = Boy(w, me + new Vector3(0.4f, 1.8f, 0f)), nob = Boy(w, me + new Vector3(-0.4f, 1.8f, 0f), MusouEnemyType.Nob);
        for (int t = 0; t < 30; t++) Tick(w, buttons: t < 3 ? InputButtons.Melee : InputButtons.None);
        Assert.Equal(MusouEnemyState.Knocked, State(w, boy));
        Assert.Equal(MusouEnemyState.Spawning, State(w, nob));   // a light step does not stagger an elite
        for (int t = 0; t < 40; t++) Tick(w, buttons: t < 3 ? InputButtons.MeleeHeavy : InputButtons.None);
        Assert.Equal(MusouEnemyState.Stagger, State(w, nob));
    }

    // ---------------------------------------------------------------- piercing (item 24)

    [Theory]
    [InlineData("sm_mp_exotic_las_cannon", WeaponSlot.Ranged, 8)]
    [InlineData("csm_mp_exotic_las_cannon", WeaponSlot.Ranged, 8)]
    [InlineData("sm_mp_exotic_heavy_bolter", WeaponSlot.Ranged, 5)]
    [InlineData("sm_mp_exotic_plasma_cannon", WeaponSlot.Ranged, 5)]
    [InlineData("sm_mp_ranged_sniper_rifle", WeaponSlot.Ranged, 4)]
    [InlineData("sm_mp_ranged_storm_bolter", WeaponSlot.Ranged, 3)]
    [InlineData("sm_mp_ranged_melta", WeaponSlot.Ranged, 3)]
    [InlineData("sm_mp_ranged_bolter", WeaponSlot.Ranged, 2)]
    [InlineData("sm_mp_ranged_plasma_gun", WeaponSlot.Ranged, 2)]
    [InlineData("sm_mp_ranged_grenade_launcher", WeaponSlot.Ranged, 2)]
    [InlineData("sm_mp_pistol_bolt_pistol", WeaponSlot.Pistol, 1)]
    public void Heavier_weapons_pierce_more(string name, WeaponSlot slot, int pierced) =>
        Assert.Equal(pierced, TfwCombatRules.Pierced(Rifle with { Name = name, Slot = slot }));

    [Fact]
    public void A_shot_goes_through_as_many_as_the_weapon_pierces_for_less_each_and_the_tracer_ends_on_the_last()
    {
        var w = War();
        w.Musou!.Frozen = true;
        var me = w.Players[Human].Position;
        var line = Enumerable.Range(0, 5).Select(k => Boy(w, me + new Vector3(0f, 6f + k * 2f, 0f))).ToArray();
        Tick(w, buttons: InputButtons.Fire);
        for (int k = 0; k < 10 && w.LastShots.Count == 0; k++) Tick(w, buttons: InputButtons.Fire);
        var healths = line.Select(id => { w.Musou.TryGet(id, out var e); return e.Health; }).ToArray();
        Assert.Equal(1 + TfwCombatRules.Pierced(Rifle), healths.Count(h => h < 1000f));
        Assert.True(1000f - healths[1] < 1000f - healths[0] && 1000f - healths[2] < 1000f - healths[1], string.Join(", ", healths));
        Assert.Equal(1000f, healths[3]);
        Assert.Equal(3, w.TfwBestPierce);
        Assert.InRange(w.LastShots[^1].End.Y - me.Y, 9.3f, 11f);
    }

    [Fact]
    public void A_bursting_projectile_goes_through_then_bursts_on_the_next_enemy()
    {
        var w = War(primary: 1);
        w.Musou!.Frozen = true;
        var me = w.Players[Human].Position;
        var line = Enumerable.Range(0, 4).Select(k => Boy(w, me + new Vector3(0f, 5f + k * 3f, 0f))).ToArray();
        DetonationEvent? burst = null;
        for (int k = 0; k < 60 && burst is null; k++)
        {
            Tick(w, buttons: k < 2 ? InputButtons.Fire : InputButtons.None);
            if (w.LastDetonations.Count > 0) burst = w.LastDetonations[0];
        }
        Assert.NotNull(burst);
        Assert.InRange(burst!.Value.Position.Y - me.Y, 10f, 12f);   // the third of the line, past the launcher's two
    }
}
