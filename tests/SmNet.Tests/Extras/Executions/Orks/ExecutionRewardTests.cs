using System.Numerics;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Executions.Orks;

/// <summary>Executions X4: the reward of a completed Warfront execution (full health, full armour and a one-bar overshield) and the overshield's rules.</summary>
public class ExecutionRewardTests
{
    /// <summary>A rig with one Boy ahead, the executor hurt to <paramref name="health"/> and <paramref name="shield"/>, the move run out; the world and the end.</summary>
    internal static (World W, ExecutionEnded End) Executed(float health = 30f, float shield = 0f, World? world = null)
    {
        var w = world ?? ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ExecutionOrkKit.Settle(w, boy);
        w.ExecutionsSetVitalsForTests(1, health, shield);
        ExecutionOrkKit.Tap(w);
        Assert.Single(w.ExecutionsStartedThisTick);
        var end = ExecutionOrkKit.RunOut(w);
        Assert.NotNull(end);
        return (w, end!.Value);
    }

    static BodyStats Body(World w) => Loadouts.Body(w.Config, w.Players[1]);

    [Fact]
    public void DoneWithKillGivesFullHealthAnd200Armour()
    {
        var (w, end) = Executed();
        Assert.Equal(ExecutionEndReason.Done, end.Reason);
        var body = Body(w);
        Assert.True(body.ShieldMax > 0f);
        Assert.Equal(body.HealthMax, w.Players[1].Health);
        Assert.Equal(2f * body.ShieldMax, w.Players[1].Shield);
        Assert.Equal(body.ShieldMax, ExecutionRules.Overshield(w.Players[1].Shield, body.ShieldMax));
        var reward = Assert.Single(w.ExecutionsRewardedThisTick);
        Assert.Equal(1, reward.Player);
        Assert.Equal(w.Tick + (uint)ExecutionRules.CooldownTicks(w.Config), reward.ReadyAt);
    }

    [Fact]
    public void InterruptedGivesNothing()
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ExecutionOrkKit.Settle(w, boy);
        w.ExecutionsSetVitalsForTests(1, 30f, 0f);
        ExecutionOrkKit.Tap(w);
        Assert.Single(w.ExecutionsStartedThisTick);
        ExecutionOrkKit.Idle(w);
        w.ExecutionsAbort(1, ExecutionEndReason.Invalid);
        ExecutionOrkKit.Idle(w);
        Assert.Equal(ExecutionEndReason.Invalid, Assert.Single(w.ExecutionsEndedThisTick).Reason);
        Assert.Empty(w.ExecutionsRewardedThisTick);
        Assert.True(w.Players[1].Health <= 30f + 1e-3f || w.Players[1].Health < Body(w).HealthMax);
        Assert.True(w.Players[1].Shield <= Body(w).ShieldMax);
        Assert.Equal(0u, w.ExecutionsReadyAt(1));
        Assert.False(w.Musou!.ExecutionsIsHeld(boy));
    }

    [Fact]
    public void OvershieldDepletedBeforeArmourAndHealth()
    {
        var (w, _) = Executed();
        var p = w.Players[1];
        var body = Body(w);
        var (after, shieldDamage, healthDamage) = Combat.ApplyDamage(p, body.ShieldMax * 0.5f, 1f, 1f, w.Config);
        Assert.Equal(0f, healthDamage);
        Assert.Equal(body.ShieldMax * 0.5f, shieldDamage, 3);
        Assert.Equal(body.HealthMax, after.Health);
        Assert.True(after.Shield > body.ShieldMax);   // still in the overshield: the armour bar is whole
    }

    [Fact]
    public void NoDecayOver60s()
    {
        var (w, _) = Executed();
        float shield = w.Players[1].Shield;
        for (int k = 0; k < 60 * w.Config.TickRate; k++) { ExecutionOrkKit.KillOthers(w); ExecutionOrkKit.Idle(w); }
        Assert.Equal(shield, w.Players[1].Shield);
        Assert.True(w.Config.KeepsOvershield);
    }

    [Fact]
    public void RefreshToCapNeverStacks()
    {
        var (w, _) = Executed();
        var body = Body(w);
        for (int k = 0; k < ExecutionRules.CooldownTicks(w.Config) + 2; k++) ExecutionOrkKit.Idle(w);
        var (_, end) = Executed(body.HealthMax, 2f * body.ShieldMax, w);
        Assert.Equal(ExecutionEndReason.Done, end.Reason);
        Assert.Equal(2f * body.ShieldMax, w.Players[1].Shield);   // 200 %, never 300 %
    }

    /// <summary>X4 phase 3 (series-37 integration): a melee strike's heal (<c>World.MusouRestore</c>, private: called by reflection) gives health back and never trims the overshield.</summary>
    [Fact]
    public void MeleeHealKeepsOvershield()
    {
        var (w, _) = Executed();
        var body = Body(w);
        w.ExecutionsSetVitalsForTests(1, body.HealthMax * 0.5f, body.ShieldMax * 1.5f);
        var restore = typeof(World).GetMethod("MusouRestore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        restore.Invoke(w, [1, 0.25f, 0.2f]);
        Assert.Equal(body.HealthMax * 0.75f, w.Players[1].Health, 3);
        Assert.Equal(body.ShieldMax * 1.5f, w.Players[1].Shield, 3);   // kept, not cut to the bar
        w.ExecutionsSetVitalsForTests(1, body.HealthMax * 0.5f, body.ShieldMax * 0.5f);
        restore.Invoke(w, [1, 0.25f, 0.2f]);
        Assert.Equal(body.ShieldMax * 0.7f, w.Players[1].Shield, 3);   // under the bar the heal adds as before
    }

    // LevelUpRefillKeepsOvershield lives in tests/SmNet.Tests/Extras/Warfront/Campaign/WarfrontRefillOvershieldTests.cs: it reads the Warfront progression, which -p:SmWarfront=false drops with the Extras/Warfront folders.

    [Fact]
    public void DeathClearsOvershield()
    {
        var (w, _) = Executed();
        Assert.True(w.TfwKillForTests(1));
        Assert.True(w.TfwRespawnAt(1, w.Players[1].Position, 0f, faceCentre: false));
        Assert.Equal(Body(w).ShieldMax, w.Players[1].Shield);   // a fresh life: the plain bar
    }

    // SaveNeverWritesOvershield lives in tests/SmNet.Tests/Extras/Warfront/Campaign/WarfrontSaveNoVitalsTests.cs (the Warfront namespace stays in its own folders).

    [Fact]
    public void ClassicNeverExceedsMax()
    {
        Assert.False(ExecutionTestKit.Config().KeepsOvershield);   // Classic PvP Extended
        Assert.False((ExecutionOrkKit.Config() with { Extras = ContentExtras.Musou }).KeepsOvershield);   // Musou Mode
        Assert.False((ExecutionOrkKit.Config() with { Extras = ContentExtras.None }).KeepsOvershield);   // faithful
        Assert.True(ExecutionOrkKit.Config().KeepsOvershield);   // Warfront
    }
}
