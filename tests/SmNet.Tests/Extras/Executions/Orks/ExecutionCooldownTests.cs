using System.Numerics;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Executions.Orks;

/// <summary>Executions X4: the 10 s Warfront cooldown, counted from a completed execution (spec "Added content: Executions: Warfront (X4)").</summary>
public class ExecutionCooldownTests
{
    /// <summary>After a completed execution, steps idle until <paramref name="seconds"/> after its end, places a fresh Boy and taps; whether a run started.</summary>
    static (World W, bool Started) TapAfter(float seconds)
    {
        var (w, end) = ExecutionRewardTests.Executed();
        Assert.Equal(ExecutionEndReason.Done, end.Reason);
        uint ended = end.Tick;
        uint at = ended + (uint)MathF.Round(seconds * w.Config.TickRate);
        ushort boy = ExecutionOrkKit.OrkAhead(w, gap: 1.3f, side: 0.6f);
        ExecutionOrkKit.Settle(w, boy);
        while (w.Tick + 1 < at) { ExecutionOrkKit.KillOthers(w, boy); ExecutionOrkKit.Idle(w); }
        Assert.True(w.Tick + 1 == at, $"the settle overran the press tick ({w.Tick + 1} > {at})");
        ExecutionOrkKit.Tap(w);
        return (w, w.ExecutionsStartedThisTick.Any(r => r.Executor == 1));
    }

    [Fact]
    public void RefusedAt9p9s()
    {
        var (w, started) = TapAfter(9.9f);
        Assert.False(started);
        Assert.Equal(ExecutionRefusal.Refused, ExecutionOrkKit.RefusedLast(w));
        Assert.Equal(ExecutionWarfrontRefusal.Cooldown, w.ExecutionsWarfrontRefusedLast(1));
    }

    [Fact]
    public void AcceptedAt10p0s()
    {
        var (_, started) = TapAfter(10f);
        Assert.True(started);
    }

    [Fact]
    public void InterruptedStartsNoCooldown()
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ExecutionOrkKit.Settle(w, boy);
        ExecutionOrkKit.Tap(w);
        Assert.Single(w.ExecutionsStartedThisTick);
        ExecutionOrkKit.Idle(w);
        w.ExecutionsAbort(1, ExecutionEndReason.Invalid);
        ExecutionOrkKit.Idle(w);
        Assert.Equal(0u, w.ExecutionsReadyAt(1));
        ExecutionOrkKit.Tap(w);
        Assert.Equal(boy, (ushort)Assert.Single(w.ExecutionsStartedThisTick).Target);   // straight away: no cooldown
    }

    /// <summary>Lead 40: a death that the match never counts (a test aid's kill between ticks) still clears the cooldown at the next step, as the player is seen dead.</summary>
    [Fact]
    public void AnUncountedDeathClearsCooldownAtTheNextStep()
    {
        var (w, _) = ExecutionRewardTests.Executed();
        Assert.NotEqual(0u, w.ExecutionsReadyAt(1));
        Assert.True(w.TfwKillForTests(1));
        ExecutionOrkKit.Idle(w);
        Assert.Equal(0u, w.ExecutionsReadyAt(1));
    }

    /// <summary>Lead 40: Warfront's own respawn (downed, the revive window, the spectator time, then the landing zone with another human standing) always comes back without a cooldown.</summary>
    [Fact]
    public void RespawnInWarfrontAlwaysClearsCooldown()
    {
        var w = ExecutionOrkKit.Rig(humans: 2);
        ExecutionOrkKit.Place(w, 2, w.Players[1].Position + new System.Numerics.Vector3(6f, 0f, 0f));   // the second human stands clear of the move
        Assert.Equal(ExecutionEndReason.Done, ExecutionRewardTests.Executed(world: w).End.Reason);
        Assert.NotEqual(0u, w.ExecutionsReadyAt(1));
        Assert.True(w.TfwKillForTests(1));
        bool respawned = false;
        for (int k = 0; k < 120 * w.Config.TickRate && !respawned; k++)
        {
            ExecutionOrkKit.KillOthers(w);
            ExecutionOrkKit.Idle(w);
            respawned = w.Players[1].Alive;
        }
        Assert.True(respawned, "the human never came back");
        Assert.Equal(0u, w.ExecutionsReadyAt(1));   // back without a cooldown
    }

    [Fact]
    public void DeathClearsCooldown()
    {
        var (w, _) = ExecutionRewardTests.Executed();
        Assert.NotEqual(0u, w.ExecutionsReadyAt(1));
        int deaths = w.Scores.TryGetValue(1, out var s) ? s.Deaths : 0;
        // A real death (counted by the match): one hit point and no armour among Boyz, the pool running.
        w.ExecutionsSetVitalsForTests(1, 1f, 0f);
        ushort[] boyz = [ExecutionOrkKit.OrkAhead(w, gap: 1.0f), ExecutionOrkKit.OrkAhead(w, gap: 1.0f, side: 0.8f), ExecutionOrkKit.OrkAhead(w, gap: 1.0f, side: -0.8f)];
        w.Musou!.Frozen = false;
        for (int k = 0; k < 30 * 30 && w.Players[1].Alive; k++) { ExecutionOrkKit.KillOthers(w, boyz); ExecutionOrkKit.Idle(w); }
        Assert.False(w.Players[1].Alive, "the Boyz never killed the player");
        Assert.Equal(deaths + 1, w.Scores[1].Deaths);
        Assert.Equal(0u, w.ExecutionsReadyAt(1));
    }
}
