using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Executions.Orks;

/// <summary>
/// Executions X4: a Warfront executor takes no damage during the move and normal damage after it. Every hit on a player (an Ork's melee, a shoota, a rokkit, a Nob's or a boss's blow, a slam, a
/// blast, a shot, a projectile, a Guard's friendly fire) is scaled by <see cref="World.DamageTakenScale"/> (DamageScale.cs; World.Musou.cs's MusouHit, Simulation.cs, RushWorld.cs, Projectiles),
/// whose executions hook gives 0 for a live run. Sources that do not pass through it: a kill volume or a fall out of the level (no damage: the player is placed or killed by the level), not gated
/// (a kill volume is a kill volume); the Bomb Squig is absent from Warfront (<c>WarfrontRules.NoSquigs</c>).
/// </summary>
public class ExecutionDamageSourcesTests
{
    [Fact]
    public void The_scale_is_zero_during_and_one_after()
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ExecutionOrkKit.Settle(w, boy);
        Assert.Equal(1f, w.DamageTakenScale(1));
        ExecutionOrkKit.Tap(w);
        Assert.True(w.ExecutionsLocked(1));
        Assert.Equal(0f, w.DamageTakenScale(1));
        Assert.Equal(1f, w.DamageTakenScale(2));
        Assert.NotNull(ExecutionOrkKit.RunOut(w));
        Assert.Equal(1f, w.DamageTakenScale(1));
    }

    public static TheoryData<MusouEnemyType> Attackers() => new() { MusouEnemyType.Boy, MusouEnemyType.ShootaBoy, MusouEnemyType.RokkitBoy, MusouEnemyType.Nob, MusouEnemyType.BossNob };

    /// <summary>One case per Ork attacker standing beside a locked executor with the pool running: nothing lands during the move; the same Ork's attacks land after it.</summary>
    [Theory]
    [MemberData(nameof(Attackers))]
    public void An_ork_beside_the_executor_does_nothing_during_the_move(MusouEnemyType attacker)
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ushort other = ExecutionOrkKit.OrkAhead(w, attacker, gap: 1.2f, side: 1.0f, health: 1e6f);
        ExecutionOrkKit.Settle(w, boy, other);
        ExecutionOrkKit.Tap(w);
        Assert.Single(w.ExecutionsStartedThisTick);
        w.Musou!.Frozen = false;
        float health = w.Players[1].Health, shield = w.Players[1].Shield;
        ExecutionEnded? end = null;
        for (int k = 0; k < 300 && end is null; k++)
        {
            ExecutionOrkKit.KillOthers(w, boy, other);
            ExecutionOrkKit.Idle(w);
            if (w.ExecutionsEndedThisTick.FirstOrDefault(e => e.Run.Executor == 1) is { Run.Executor: 1 } e) end = e;
            else
            {
                Assert.True(w.Players[1].Health >= health, $"{attacker} hurt the executor during the move");
                Assert.True(w.Players[1].Shield >= shield, $"{attacker} took armour during the move");
            }
        }
        Assert.NotNull(end);
        Assert.Equal(ExecutionEndReason.Done, end!.Value.Reason);
    }
}
