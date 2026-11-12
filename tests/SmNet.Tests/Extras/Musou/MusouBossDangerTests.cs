using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Owner, 2026-11-12 (Musou Mode): the Warboss and the Boss Orks hit 1.5x harder but never kill a full player in one hit (a cap per hit of 75 % of his full health plus armour); a boss is
/// never stunned by its health falling (rolled back the same day) nor by any non-melee stun, only by a player's melee stagger (its ability dropped, nothing started); only elites and bosses drop powerups.
/// </summary>
public class MusouBossDangerTests
{
    static readonly MusouEnemyType[] Bosses =
        [MusouEnemyType.Warboss, MusouEnemyType.BossBoy, MusouEnemyType.BossShootaBoy, MusouEnemyType.BossArdBoy, MusouEnemyType.BossNob];

    // ---------------------------------------------------------------- danger

    [Fact]
    public void Boss_damage_is_one_and_a_half_times_what_it_was_and_every_attack_stays_under_a_full_players_health_plus_armour()
    {
        Assert.Equal(1.5f, MusouBossRules.DangerScale);
        var rules = MusouRules.Default;
        var boss = MusouBossRules.Default;
        Assert.Equal(90f, rules.Of(MusouEnemyType.Warboss).MeleeDamage);   // 60 before
        Assert.Equal(90f, boss.SlamDamage);   // 60 before
        Assert.Equal(112.5f, boss.ChargeDamage);   // 75 before
        Assert.Equal(8f * 3f, rules.Of(MusouEnemyType.BossBoy).MeleeDamage);   // 2x the Boy's 8 before, now 3x
        Assert.Equal(25f * 3f, rules.Of(MusouEnemyType.BossNob).MeleeDamage);
        Assert.Equal(10f * 3f, rules.Of(MusouEnemyType.BossArdBoy).MeleeDamage);
        Assert.Equal(3f * 3f, rules.Of(MusouEnemyType.BossShootaBoy).RangedDamage);

        // The default body (100 health, 100 armour): the strongest single attack is under the pool, and the cap (75 %) is under it still.
        var config = new SimConfig();
        float pool = config.HealthMax + config.ShieldMax;
        float strongest = new[] { boss.SlamDamage, boss.ChargeDamage }.Concat(Bosses.SelectMany(t => new[] { rules.Of(t).MeleeDamage, rules.Of(t).RangedDamage })).Max();
        Assert.True(strongest < pool, $"{strongest} vs {pool}");
        Assert.True(MusouBossRules.MaxHitShare * pool < pool);
    }

    static World NewWorld(out SimConfig config)
    {
        var weapon = new WeaponSpec("bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
        var step = new MeleeStep(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
        config = MusouLoadout.Prepare(new SimConfig
        {
            Weapons = [weapon],
            Classes = [new("sm - tactical", 0, -1, [step]) { PrimaryChoices = [0], Side = Team.SpaceMarines, Role = "tactical" }],
            Extras = ContentExtras.Musou,
        });
        var w = new World(config) { Rules = MusouSimTests.Endless, StartAreas = MusouDirectorTests.Rooms };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;
        w.Musou!.Frozen = true;
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => default);
        return w;
    }

    [Fact]
    public void A_boss_hit_never_takes_more_than_the_cap_so_a_full_player_survives_but_any_other_Ork_may_kill()
    {
        var w = NewWorld(out var config);
        var body = Loadouts.Body(config, w.Players[1]);
        float pool = body.HealthMax + body.ShieldMax;
        var from = w.Players[1].Position + new Vector3(0f, 1.5f, 0f);
        ushort boss = w.Musou!.Spawn(MusouEnemyType.Warboss, new Vector3(300f, 300f, 0f), 0f, 15000f);
        ushort boy = w.Musou.Spawn(MusouEnemyType.Boy, new Vector3(305f, 300f, 0f), 0f, 85f);

        w.MusouLandAttack(new MusouAttack(boss, 1, 100000f, Melee: true, Staggers: false, from));
        var after = w.Players[1];
        Assert.True(after.Alive);
        Assert.Equal(pool * MusouBossRules.MaxHitShare, body.HealthMax + body.ShieldMax - after.Health - after.Shield, 2);

        var w2 = NewWorld(out _);
        ushort boy2 = w2.Musou!.Spawn(MusouEnemyType.Boy, new Vector3(305f, 300f, 0f), 0f, 85f);
        w2.MusouLandAttack(new MusouAttack(boy2, 1, 100000f, Melee: true, Staggers: false, w2.Players[1].Position + new Vector3(0f, 1.5f, 0f)));
        Assert.False(w2.Players[1].Alive);   // the cap is the bosses' alone
        _ = boy;
    }

    [Fact]
    public void The_real_boss_strikes_leave_a_full_player_alive()
    {
        foreach (var ability in new[] { MusouBossAbility.GroundSlam, MusouBossAbility.Charge })
        {
            var w = NewWorld(out _);
            ushort boss = w.Musou!.Spawn(MusouEnemyType.Warboss, new Vector3(300f, 300f, 0f), 0f, 15000f);
            float damage = ability == MusouBossAbility.Charge ? MusouBossRules.Default.ChargeDamage : MusouBossRules.Default.SlamDamage;
            w.MusouLandBossStrike(boss, new MusouBossStrike(1, damage, w.Players[1].Position + new Vector3(0f, 1.5f, 0f), -Vector2.UnitY, 0f, ability));
            Assert.True(w.Players[1].Alive);
            Assert.True(w.Players[1].Health + w.Players[1].Shield > 0f);
        }
    }

    // ---------------------------------------------------------------- stun

    sealed class Arena
    {
        public readonly MusouSim Sim;
        public readonly MusouTarget[] Targets;
        public readonly List<MusouAttack> Attacks = new();
        public readonly List<MusouBossStrike> Strikes = new();
        public MusouBoss? Boss;
        public uint Tick;
        public ushort Id;

        public Arena(MusouEnemyType type, float health, bool war = true, params Vector3[] players)
        {
            var rules = MusouRules.Default with
            {
                Boss = MusouBossRules.Default with { SlamFirst = 0f, ChargeFirst = 1000f },
            };
            Sim = new MusouSim(null, rules) { TickRate = 30 };
            if (war) Sim.War = TfwCombatRules.Default;
            Id = Sim.Spawn(type, Vector3.Zero, 0f, health);
            if (type == MusouEnemyType.Warboss) Boss = new MusouBoss(Sim, null, Id, health);
            Targets = [.. players.Select((p, k) => new MusouTarget(k + 1, p, true))];
            for (int k = 0; k < 60; k++) Step();   // out of its spawn stand
        }

        public void Step()
        {
            Sim.BeginTick(++Tick);
            Attacks.Clear(); Strikes.Clear();
            Sim.Step(Targets, null, true, Attacks);
            Boss?.Step(Targets, true, Strikes);
        }

        public MusouEnemy Me => Sim.TryGet(Id, out var e) ? e : default;
        public void Hit(float amount) => Sim.Damage(Id, 1, amount, Vector3.Zero, out _);
        public void HitTo(float share, float max) => Hit(Me.Health - max * share);
    }

    static readonly Vector2 Push = Vector2.UnitY;

    [Fact]
    public void A_boss_is_not_stunned_at_80_60_40_and_20_percent_of_its_health()
    {
        foreach (var type in Bosses)
        {
            var a = new Arena(type, 1000f, true, new Vector3(0f, 30f, 0f));
            foreach (float share in new[] { 0.79f, 0.59f, 0.39f, 0.19f, 0.05f })
            {
                a.HitTo(share, 1000f);
                for (int k = 0; k < 3; k++)
                {
                    a.Step();
                    Assert.NotEqual(MusouEnemyState.Stunned, a.Me.State);
                    Assert.NotEqual(MusouEnemyState.Stagger, a.Me.State);
                }
                Assert.False(a.Sim.BossStunned(a.Id));
            }
            Assert.Equal(0, a.Sim.BossStuns);
        }
    }

    [Fact]
    public void No_stagger_that_is_not_a_melee_strike_moves_a_boss()
    {
        // Grenades, blasts, demolition charges, plasma, artillery, the drop pod and the Librarian's Shockwave all go through Stagger (or a KnockDown that is false for a boss): false for every boss.
        foreach (var type in Bosses)
        {
            var a = new Arena(type, 1000f, true, new Vector3(0f, 30f, 0f));
            Assert.False(a.Sim.Stagger(a.Id, Push, 2f));
            Assert.False(a.Sim.Stagger(a.Id, Push, 0f));
            Assert.False(a.Sim.KnockDown(a.Id, Push, 2f, 1, 0f));
            a.Step();
            Assert.NotEqual(MusouEnemyState.Stunned, a.Me.State);
            Assert.NotEqual(MusouEnemyState.Stagger, a.Me.State);
            Assert.Equal(0, a.Sim.BossStuns);
        }
    }

    [Fact]
    public void A_melee_stagger_stuns_every_boss_for_the_staggers_own_length_and_it_stands()
    {
        foreach (var type in Bosses)
        {
            var a = new Arena(type, 4000f, true, new Vector3(0f, 6f, 0f));
            var at = a.Me.Position;
            Assert.True(a.Sim.StaggerMelee(a.Id, Push, 2f));
            Assert.Equal(MusouEnemyState.Stunned, a.Me.State);
            Assert.Equal(1, a.Sim.BossStuns);
            int window = (int)MathF.Round(MusouRules.Default.Combat.StaggerSeconds * 30f);   // 0.6 s = 18 ticks
            for (int k = 0; k < window - 1; k++) { a.Step(); Assert.Equal(MusouEnemyState.Stunned, a.Me.State); Assert.Empty(a.Attacks); }
            a.Step();
            Assert.NotEqual(MusouEnemyState.Stunned, a.Me.State);
            Assert.Equal(at, a.Me.Position);   // no knockback: it stood where it was
        }
    }

    [Fact]
    public void A_melee_stun_drops_the_Warbosss_ability_under_way_and_nothing_starts_until_it_ends()
    {
        var a = new Arena(MusouEnemyType.Warboss, 4000f, true, new Vector3(0f, 3f, 0f));
        int guard = 0;
        while (a.Boss!.Action != MusouBossAction.SlamWindUp && guard++ < 400) a.Step();
        Assert.Equal(MusouBossAction.SlamWindUp, a.Boss!.Action);
        Assert.True(a.Sim.StaggerMelee(a.Id, Push, 2f));
        for (int k = 0; k < 17; k++)
        {
            a.Step();
            Assert.Empty(a.Strikes);   // the slam that was winding up never lands
            Assert.Equal(MusouBossAction.None, a.Boss.Action);
            Assert.Equal(MusouEnemyState.Stunned, a.Me.State);
        }
    }

    [Fact]
    public void A_stunned_boss_takes_ordinary_damage_and_the_melee_stun_is_the_wars_only()
    {
        var a = new Arena(MusouEnemyType.BossNob, 1000f, true, new Vector3(0f, 30f, 0f));
        a.Sim.StaggerMelee(a.Id, Push, 2f);
        float health = a.Me.Health;
        a.Hit(100f);
        Assert.Equal(health - 100f, a.Me.Health, 2);
        var skirmish = new Arena(MusouEnemyType.Warboss, 1000f, false, new Vector3(0f, 30f, 0f));   // the 5-wave run's Warboss (its replay is pinned): no stun
        Assert.False(skirmish.Sim.StaggerMelee(skirmish.Id, Push, 2f));
        Assert.Equal(0, skirmish.Sim.BossStuns);
        var nob = new Arena(MusouEnemyType.Nob, 1000f, true, new Vector3(0f, 30f, 0f));   // an ordinary Ork staggers as always
        Assert.True(nob.Sim.StaggerMelee(nob.Id, Push, 2f));
        Assert.Equal(MusouEnemyState.Stagger, nob.Me.State);
        Assert.Equal(0, nob.Sim.BossStuns);
    }

    [Fact]
    public void Stun_lock_measurement_chained_melee_stuns_hold_a_boss_as_long_as_each_lands_inside_the_window_and_no_longer()
    {
        // Owner's call whether to add an immunity window; none exists. The window is the stagger's 0.6 s (18 ticks); each melee stagger restarts it.
        int window = (int)MathF.Round(MusouRules.Default.Combat.StaggerSeconds * 30f);
        Assert.Equal(18, window);
        foreach (var (gap, held) in new[] { (18, true), (12, true), (19, false) })
        {
            var a = new Arena(MusouEnemyType.Warboss, 1e7f, true, new Vector3(0f, 6f, 0f));
            int freeTicks = 0;
            const int Ticks = 900;   // 30 s of chained strikes
            for (int t = 0; t < Ticks; t++)
            {
                if (t % gap == 0) a.Sim.StaggerMelee(a.Id, Push, 2f);
                if (a.Me.State != MusouEnemyState.Stunned) freeTicks++;   // a tick the boss starts free: after this tick's strike, before its step
                a.Step();
            }
            if (held) Assert.Equal(0, freeTicks);   // held the whole 30 s: unbounded for as long as the strikes keep coming
            else Assert.InRange(freeTicks, 1, Ticks / gap + 1);   // one free tick between strikes at most
        }
    }

    // ---------------------------------------------------------------- drops

    [Fact]
    public void Only_elites_and_bosses_drop_powerups()
    {
        foreach (var type in Enum.GetValues<MusouEnemyType>())
        {
            bool eligible = MusouPowerupRules.Boss(type) || MusouPowerupRules.Elite(type);
            int drops = 0;
            for (uint t = 1; t <= 2000; t++) if (MusouPowerupRules.Drops(type, t, (ushort)(t % 900 + 1))) drops++;
            if (!eligible) Assert.Equal(0, drops);
            else Assert.True(drops > 0, type.ToString());
        }
        Assert.Equal(0.1f, MusouPowerupRules.EliteChance);   // kept: more bosses already mean more drops
    }
}
