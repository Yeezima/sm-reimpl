using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The owner, 2026-11-06: "remove all stun effects by orks in musou mode. they should never impede player movement or action". Every way an enemy hurts a player in Musou Mode (each kind's melee strike, the
/// shooters' rounds, the Warboss's slam and charge) deals its damage and leaves the player free to move and fire on the same tick; with the old rule switched back on the Nob's strike and the charge still stagger
/// (so the test sees a stagger when there is one); classic PvP has no Musou at all, so none of this reaches it.
/// </summary>
public class TfwNoStunTests
{
    const int Human = TfwRig.Human;

    public static TheoryData<MusouEnemyType, bool> Attacks => new()
    {
        { MusouEnemyType.Boy, true }, { MusouEnemyType.ShootaBoy, true }, { MusouEnemyType.ShootaBoy, false }, { MusouEnemyType.ArdBoy, true }, { MusouEnemyType.Gretchin, true },
        { MusouEnemyType.Nob, true }, { MusouEnemyType.ChaosMarine, true }, { MusouEnemyType.ChaosMarine, false }, { MusouEnemyType.Warboss, true },
        { MusouEnemyType.BossBoy, true }, { MusouEnemyType.BossShootaBoy, false }, { MusouEnemyType.BossArdBoy, true }, { MusouEnemyType.BossNob, true },
    };

    /// <summary>A war with the human landed and the field cleared, the enemy's attack landing in front of him.</summary>
    static (World W, ushort Enemy) Landed(MusouEnemyType type, bool impede = false)
    {
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        TfwRig.KillAll(w);
        TfwRig.Steps(w, 2);
        if (impede) w.Musou!.War = TfwCombatRules.Default with { EnemiesImpede = true };
        var me = w.Players[Human];
        ushort enemy = w.Musou!.Spawn(type, me.Position + new Vector3(0f, 1.2f, 0f), MathF.PI, 1e6f);
        Assert.NotEqual(0, enemy);
        return (w, enemy);
    }

    /// <summary>The tick after the hit, the player walks forward and pulls the trigger: he moves at once and nothing holds him.</summary>
    static void AssertFree(World w, PlayerState hit)
    {
        Assert.Equal(ReactionKind.None, hit.Reaction);
        Assert.Equal(0, hit.PushTicks);
        Assert.DoesNotContain(w.LastReactions, r => r.Target == Human);
        var before = hit.Position;
        w.Step(id => new PlayerInput(w.Tick, 0f, 1f, w.Players[id].Yaw, id == Human ? InputButtons.Fire : InputButtons.None));
        var after = w.Players[Human];
        Assert.Equal(ReactionKind.None, after.Reaction);
        Assert.DoesNotContain(w.LastReactions, r => r.Target == Human);
        Assert.True(Vector3.Distance(before, after.Position) > 0.01f, "the player moved on the next tick");
        Assert.Contains(w.LastShots, s => s.Shooter == Human);
    }

    [Theory]
    [MemberData(nameof(Attacks))]
    public void An_enemy_attack_of_every_kind_hurts_and_never_stuns(MusouEnemyType type, bool melee)
    {
        var (w, enemy) = Landed(type);
        var spec = w.Musou!.Rules.Of(type);
        var before = w.Players[Human];
        float damage = melee ? (spec.Melee ? spec.MeleeDamage : 10f) : (spec.Ranged ? spec.RangedDamage : 3f);
        w.MusouLandAttack(new MusouAttack(enemy, Human, damage, melee, melee && spec.MeleeStaggers, before.Position + new Vector3(0f, 1.2f, 0f)));
        var hit = w.Players[Human];
        Assert.True(hit.Health + hit.Shield < before.Health + before.Shield, "the damage stays");
        AssertFree(w, hit);
    }

    [Theory]
    [InlineData(MusouBossAbility.GroundSlam)]
    [InlineData(MusouBossAbility.Charge)]
    public void The_warboss_slam_and_charge_hurt_and_never_stun_or_throw(MusouBossAbility ability)
    {
        var (w, boss) = Landed(MusouEnemyType.Warboss);
        var rules = w.Musou!.Rules.Boss;
        var before = w.Players[Human];
        float knockback = ability == MusouBossAbility.Charge ? rules.ChargeKnockback : 0f;
        w.MusouLandBossStrike(boss, new MusouBossStrike(Human, ability == MusouBossAbility.Charge ? rules.ChargeDamage : rules.SlamDamage, before.Position + new Vector3(0f, 1.5f, 0f), -Vector2.UnitY, knockback, ability));
        var hit = w.Players[Human];
        Assert.True(hit.Health + hit.Shield < before.Health + before.Shield, "the damage stays");
        AssertFree(w, hit);
    }

    [Fact]
    public void With_the_old_rule_back_a_nob_and_the_charge_stagger_so_the_test_would_see_it()
    {
        var (w, nob) = Landed(MusouEnemyType.Nob, impede: true);
        var me = w.Players[Human];
        w.MusouLandAttack(new MusouAttack(nob, Human, 25f, true, true, me.Position + new Vector3(0f, 1.2f, 0f)));
        Assert.Equal(ReactionKind.Stagger, w.Players[Human].Reaction);

        var (w2, boss) = Landed(MusouEnemyType.Warboss, impede: true);
        var at = w2.Players[Human].Position;
        w2.MusouLandBossStrike(boss, new MusouBossStrike(Human, 50f, at + new Vector3(0f, 1.5f, 0f), -Vector2.UnitY, 3f, MusouBossAbility.Charge));
        Assert.Equal(ReactionKind.Stagger, w2.Players[Human].Reaction);
        Assert.True(w2.Players[Human].PushDistance > 0);
    }

    [Fact]
    public void The_barrage_the_players_call_keeps_its_stagger()
    {
        var (w, _) = Landed(MusouEnemyType.Boy);
        var me = w.Players[Human];
        w.MusouLandAttack(new MusouAttack(0, Human, 10f, false, true, me.Position + new Vector3(0f, 2f, 0f)));   // enemy 0: the players' own shells (World.TfwFire.cs)
        Assert.Equal(ReactionKind.Stagger, w.Players[Human].Reaction);
    }

    [Fact]
    public void Classic_pvp_has_no_musou_so_the_rule_never_reaches_it()
    {
        var config = TfwRig.Config with { Extras = ContentExtras.None };
        var w = new World(config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, 5f, 0f), MathF.PI)]) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f } };
        w.Spawn(1, 0);
        w.Spawn(2, 1);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) w.Step(_ => default);
        Assert.Null(w.Musou);
        Assert.False(w.StartTfw(null, TfwTestArena.Field));
        var before = w.Players[1];
        w.MusouLandAttack(new MusouAttack(5, 1, 50f, true, true, new Vector3(0f, 1f, 0f)));
        w.MusouLandBossStrike(5, new MusouBossStrike(1, 50f, new Vector3(0f, 1f, 0f), -Vector2.UnitY, 3f, MusouBossAbility.Charge));
        Assert.Equal(before, w.Players[1]);   // nothing at all: the Musou hooks return at once (MusouIsolationTests pins the classic replay)
    }
}
