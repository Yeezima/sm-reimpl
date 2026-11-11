using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Owner, 2026-11-12 (Musou Mode): the Warboss and the Boss Orks hit 1.5x harder but never kill a full player in one hit (a cap per hit of 75 % of his full health plus armour); a boss that falls to 80, 60,
/// 40 or 20 % of its health stands stunned for 5 s (each share once, in the war only), its ability dropped and nothing started; only elites and bosses drop powerups.
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

    [Fact]
    public void A_boss_is_stunned_five_seconds_each_time_it_falls_to_80_60_40_and_20_percent()
    {
        foreach (var type in Bosses)
        {
            var a = new Arena(type, 1000f, true, new Vector3(0f, 30f, 0f));
            Assert.NotEqual(MusouEnemyState.Stunned, a.Me.State);
            a.HitTo(0.81f, 1000f);   // not yet
            a.Step();
            Assert.Equal(0, a.Sim.BossStuns);
            Assert.NotEqual(MusouEnemyState.Stunned, a.Me.State);
            foreach (float share in new[] { 0.8f, 0.6f, 0.4f, 0.2f })
            {
                a.HitTo(share - 0.001f, 1000f);
                Assert.Equal(MusouEnemyState.Stunned, a.Me.State);
                // 5 s = 150 ticks: still stunned after 149 more steps, back to its own decisions after the 150th.
                for (int k = 0; k < 149; k++) { a.Step(); Assert.Equal(MusouEnemyState.Stunned, a.Me.State); }
                a.Step();
                Assert.NotEqual(MusouEnemyState.Stunned, a.Me.State);
                a.Hit(1f);   // a little more, still above the next share: no new stun
                Assert.NotEqual(MusouEnemyState.Stunned, a.Me.State);
            }
            Assert.Equal(4, a.Sim.BossStuns);
        }
    }

    [Fact]
    public void One_hit_across_several_shares_stuns_once_and_marks_them_all()
    {
        var a = new Arena(MusouEnemyType.BossNob, 1000f, true, new Vector3(0f, 30f, 0f));
        a.HitTo(0.35f, 1000f);   // 80, 60 and 40 % crossed together
        Assert.Equal(MusouEnemyState.Stunned, a.Me.State);
        Assert.Equal(1, a.Sim.BossStuns);
        for (int k = 0; k < 150; k++) a.Step();
        a.HitTo(0.25f, 1000f);   // below the marked shares, above 20 %: nothing
        Assert.Equal(1, a.Sim.BossStuns);
        a.HitTo(0.19f, 1000f);
        Assert.Equal(2, a.Sim.BossStuns);
        Assert.Equal(MusouEnemyState.Stunned, a.Me.State);
    }

    [Fact]
    public void A_stunned_boss_neither_moves_nor_attacks_and_takes_ordinary_damage()
    {
        var player = new Vector3(0f, 6f, 0f);
        var a = new Arena(MusouEnemyType.BossNob, 10000f, true, player);
        a.HitTo(0.79f, 10000f);
        Assert.Equal(MusouEnemyState.Stunned, a.Me.State);
        var at = a.Me.Position;
        float health = a.Me.Health;
        for (int k = 0; k < 140; k++)
        {
            a.Step();
            Assert.Equal(at, a.Me.Position);
            Assert.Empty(a.Attacks);
            Assert.Equal(MusouEnemyState.Stunned, a.Me.State);
        }
        a.Hit(100f);
        Assert.Equal(health - 100f, a.Me.Health, 2);   // no extra damage in the window (ours)
        // After the window it goes for the player again.
        for (int k = 0; k < 30; k++) a.Step();
        Assert.NotEqual(at, a.Me.Position);
    }

    [Fact]
    public void A_stun_drops_the_Warbosss_ability_under_way_and_nothing_starts_until_it_ends()
    {
        var a = new Arena(MusouEnemyType.Warboss, 4000f, true, new Vector3(0f, 3f, 0f));
        // The slam is due at once (SlamFirst 0): step until it winds up.
        int guard = 0;
        while (a.Boss!.Action != MusouBossAction.SlamWindUp && guard++ < 400) a.Step();
        Assert.Equal(MusouBossAction.SlamWindUp, a.Boss!.Action);
        a.HitTo(0.79f, 4000f);
        for (int k = 0; k < 140; k++)
        {
            a.Step();
            Assert.Empty(a.Strikes);   // the slam that was winding up never lands
            Assert.Equal(MusouBossAction.None, a.Boss.Action);
            Assert.Equal(MusouEnemyState.Stunned, a.Me.State);
        }
    }

    [Fact]
    public void Only_a_boss_in_the_war_is_stunned()
    {
        var nob = new Arena(MusouEnemyType.Nob, 1000f, true, new Vector3(0f, 30f, 0f));
        nob.HitTo(0.5f, 1000f);
        Assert.Equal(0, nob.Sim.BossStuns);
        Assert.NotEqual(MusouEnemyState.Stunned, nob.Me.State);
        var skirmish = new Arena(MusouEnemyType.Warboss, 1000f, false, new Vector3(0f, 30f, 0f));   // the 5-wave run's Warboss (its replay is pinned): no stun
        skirmish.HitTo(0.5f, 1000f);
        Assert.Equal(0, skirmish.Sim.BossStuns);
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
