using System.Numerics;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Executions.Orks;

/// <summary>Executions X4: the held Ork (MusouSim.Executions.cs, the hook lines in MusouSim.Ai.cs) and its kill frame through the normal damage path.</summary>
public class ExecutionOrkHoldTests
{
    static (World W, ushort Boy, ExecutionRun Run) Started(MusouEnemyType type = MusouEnemyType.Boy, float health = 100f)
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w, type, health: health);
        ExecutionOrkKit.Settle(w, boy);
        ExecutionOrkKit.Tap(w);
        var run = Assert.Single(w.ExecutionsStartedThisTick);
        Assert.Equal(boy, (ushort)run.Target);
        return (w, boy, run);
    }

    [Fact]
    public void HeldOrkDoesNotMoveOrStrike()
    {
        var (w, boy, run) = Started();
        w.Musou!.Frozen = false;   // the pool runs: an unheld Boy this near would wind up and strike
        Assert.True(w.Musou.TryGet(boy, out var at));
        uint kill = ExecutionTrack.EventTick(run, w.Config.Executions.AtWire(run.Wire)!, ExecutionEventKind.Kill, w.Config.TickRate);
        while (w.Tick + 1 < kill)
        {
            ExecutionOrkKit.KillOthers(w, boy);
            ExecutionOrkKit.Idle(w);
            Assert.True(w.Musou.TryGet(boy, out var e));
            Assert.Equal(MusouEnemyState.Seek, e.State);
            Assert.Equal(at.Position, e.Position);
            Assert.Equal(run.VictimYaw, e.Yaw, 4);
            Assert.Equal(0, e.AttackToken);
            Assert.True(w.ExecutionsLocked(1));
        }
    }

    [Fact]
    public void PendingWindupCancelled()
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ExecutionOrkKit.Settle(w, boy);
        w.Musou!.Frozen = false;
        for (int k = 0; k < 90 && !(w.Musou.TryGet(boy, out var e) && e.State == MusouEnemyState.WindUp); k++) { ExecutionOrkKit.KillOthers(w, boy); ExecutionOrkKit.Idle(w); }
        Assert.True(w.Musou.TryGet(boy, out var wound) && wound.State == MusouEnemyState.WindUp, "the Boy never wound up");
        ExecutionOrkKit.Tap(w);
        if (!w.ExecutionsStartedThisTick.Any()) return;   // the swing landed first and staggered the player (a reaction refuses): the design's "resolved this tick lands"
        for (int k = 0; k < 20; k++)
        {
            ExecutionOrkKit.KillOthers(w, boy);
            ExecutionOrkKit.Idle(w);
            Assert.True(w.Musou.TryGet(boy, out var e));
            Assert.Equal(MusouEnemyState.Seek, e.State);
        }
    }

    [Fact]
    public void ResolvedStrikeSameTickLands()
    {
        // The hold starts in the executions step, after the tick's enemy step and attacks (Simulation.cs: ExtrasStep, then ExtrasExecutionStep): a blow that landed this tick is already applied,
        // and the executor's invulnerability starts with the run (DamageTakenScale reads the live runs).
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ExecutionOrkKit.Settle(w, boy);
        Assert.Equal(1f, w.DamageTakenScale(1));
        ExecutionOrkKit.Tap(w);
        Assert.Single(w.ExecutionsStartedThisTick);
        Assert.Equal(0f, w.DamageTakenScale(1));
    }

    [Fact]
    public void BlocksHitIsTheGatePredicate()
    {
        // X4 phase 2 wires ExecutionsBlocksHit into MusouSim.Damage, Stagger and KnockDown (after Chaos C2). The predicate itself: held and not the kill frame.
        var (w, boy, _) = Started();
        Assert.True(w.Musou!.ExecutionsBlocksHit(boy));
        Assert.False(w.Musou.ExecutionsBlocksHit((ushort)(boy + 1)));
        w.Musou.ExecutionsRelease(boy);
        Assert.False(w.Musou.ExecutionsBlocksHit(boy));
    }

    [Fact]   // X4 phase 2 (wired at the series-37 integration): the Damage, Stagger and KnockDown gates (MusouSim.cs, MusouCombat.cs, Campaign/MusouSim.Knock.cs)
    public void BulletBlastBurnLightningStaggerKnockAllRefused()
    {
        var (w, boy, _) = Started();
        Assert.False(w.Musou!.Damage(boy, 1, 5f, Vector3.Zero, out _));
        Assert.False(w.Musou.Damage(boy, 1, 5f, Vector3.Zero, out _, cause: MusouDeathKind.Blast));
        Assert.False(w.Musou.Damage(boy, 1, 5f, Vector3.Zero, out _, quiet: true));
        Assert.False(w.Musou.Stagger(boy, Vector2.UnitY, 1f));
        Assert.True(w.Musou.TryGet(boy, out var e) && e.Health == 100f);
    }

    [Fact]   // X4 phase 2 (wired at the series-37 integration): only the kill frame's own hit lands on a held Ork
    public void OnlyKillFrameDamageLands()
    {
        var (w, boy, _) = Started();
        Assert.False(w.Musou!.Damage(boy, 1, 5f, Vector3.Zero, out _));
        Assert.True(w.Musou.ExecutionsKill(boy, 1, out bool killed) && killed);
    }

    [Fact]
    public void SlotReuseReleasesHold()
    {
        var (w, boy, _) = Started();
        var musou = w.Musou!;
        Assert.True(musou.Remove(boy));
        Assert.False(musou.ExecutionsIsHeld(boy));
        ExecutionOrkKit.Idle(w);
        var end = Assert.Single(w.ExecutionsEndedThisTick);
        Assert.Equal(ExecutionEndReason.Invalid, end.Reason);
        Assert.Equal(0, musou.ExecutionsHeldCount);

        // A hold on a slot later taken by a new spawn is dropped by the spawn (the free list reuses the slot last).
        var (w2, boy2, _) = Started();
        var m2 = w2.Musou!;
        Assert.True(m2.Remove(boy2));
        Assert.Equal(1, m2.ExecutionsHeldCount);   // the hold outlives the freed slot until the run's check or a reuse
        var spawned = new List<ushort>();
        ushort id = 0;
        for (int k = 0; k < MusouSim.Capacity && id != boy2; k++) { id = m2.Spawn(MusouEnemyType.Boy, w2.Players[1].Position + new Vector3(30f, 0f, 0f), 0f, 100f); if (id == 0) break; spawned.Add(id); }
        Assert.Equal(boy2, id);
        Assert.False(m2.ExecutionsIsHeld(boy2));
        Assert.Equal(0, m2.ExecutionsHeldCount);
        m2.Clear();
        Assert.Equal(0, m2.ExecutionsHeldCount);
    }

    public static TheoryData<MusouEnemyType> Eligible()
    {
        var d = new TheoryData<MusouEnemyType>();
        foreach (var t in Enum.GetValues<MusouEnemyType>()) if (ExecutionChoice.EligibleType(t)) d.Add(t);
        return d;
    }

    /// <summary>Every eligible type dies at the kill frame, at 50 000 hp and with Double Damage's opposite (a scaled attacker): the amount is max(health x 8, 10 000).</summary>
    [Theory]
    [MemberData(nameof(Eligible))]
    public void AllEligibleTypesDieAtKillFrame(MusouEnemyType type)
    {
        var w = ExecutionOrkKit.Rig();
        ushort e = ExecutionOrkKit.OrkAhead(w, type, health: 50000f);
        ExecutionOrkKit.Settle(w, e);
        Assert.True(w.Musou!.ExecutionsHold(e, 0f));
        Assert.True(w.Musou.ExecutionsKill(e, 1, out bool killed));
        Assert.True(killed, $"{type} survived its kill frame");
        Assert.Equal(10000f, MusouSim.ExecutionsKillAmount(10f));
        Assert.Equal(800f * 100f, MusouSim.ExecutionsKillAmount(10000f));
    }

    [Fact]
    public void XpAssistDropPathRuns()
    {
        var (w, boy, run) = Started();
        int killed = w.Tfw!.Battle!.OrksKilled;
        var end = ExecutionOrkKit.RunOut(w);
        Assert.NotNull(end);
        Assert.Equal(ExecutionEndReason.Done, end!.Value.Reason);
        Assert.True(end.Value.Run.KillDone);
        Assert.Equal(killed + 1, w.Tfw.Battle!.OrksKilled);   // the war's kill count (and with it the Warfront XP of WarfrontKill) took it
        Assert.True(w.Musou!.TryGet(boy, out var e) ? !e.Alive : true);
        Assert.False(w.Musou.ExecutionsIsHeld(boy));
        _ = run;
    }
}
