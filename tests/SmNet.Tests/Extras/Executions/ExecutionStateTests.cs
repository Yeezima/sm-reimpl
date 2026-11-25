using System.Numerics;
using SmNet.Extras.Executions;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou.Campaign;
using static SmNet.Tests.Extras.Executions.ExecutionTestKit;

namespace SmNet.Tests.Extras.Executions;

/// <summary>Executions X3, the state machine: the press edge, the refusals before any choice, the lock and pin, the ends, the abort API and the flag. Synthetic rows, no level.</summary>
public class ExecutionStateTests
{
    static readonly Vector3 Near = new(0f, 1.2f, 0f);

    static (World W, uint Kill) Killed(SimConfig? c = null)
    {
        var w = Arena(c ?? Config(), (Tactical, Vector3.Zero), (Chaos, Near));
        return (w, KillByMelee(w));
    }

    static ExecutionRun Started(World w, uint kill)
    {
        Tap(w, 1, kill + 1);
        return Assert.Single(w.ExecutionsStartedThisTick);
    }

    static void Idle(World w, int ticks = 1, InputButtons held = InputButtons.None) { for (int i = 0; i < ticks; i++) Step(w, id => id == 1 ? held : InputButtons.None); }

    static ExecutionEnded RunToEnd(World w, int most = 200, Action? each = null)
    {
        for (int i = 0; i < most; i++)
        {
            Step(w, _ => InputButtons.None);
            each?.Invoke();
            if (w.ExecutionsEndedThisTick.Count > 0) return w.ExecutionsEndedThisTick[0];
        }
        throw new Xunit.Sdk.XunitException("no end");
    }

    [Fact]
    public void HeldPressNeverStarts()
    {
        // Execute held from before the swing, through the kill and the whole window: no edge after the kill, nothing starts.
        var w = Arena(Config(), (Tactical, Vector3.Zero), (Chaos, Near));
        int starts = 0;
        for (int k = 0; k < 60; k++)
        {
            Step(w, id => id == 1 ? InputButtons.Execute | (k == 1 ? InputButtons.Melee : InputButtons.None) : InputButtons.None);
            starts += w.ExecutionsStartedThisTick.Count;
        }
        Assert.Contains(w.LastKillSequence, new[] { 1 });
        Assert.Equal(0, starts);
    }

    [Fact]
    public void EdgeStartsOnce()
    {
        var (w, kill) = Killed();
        int starts = 0;
        for (int k = 0; k < 100; k++)
        {
            Step(w, id => id == 1 ? InputButtons.Execute : InputButtons.None, kill + 1);   // pressed once, then held
            starts += w.ExecutionsStartedThisTick.Count;
        }
        Assert.Equal(1, starts);
    }

    [Fact]
    public void AirbornePressRefusedWithoutChoice()
    {
        var w = Arena(Config(), Flat(), (Tactical, Vector3.Zero), (Chaos, Near));
        uint kill = KillByMelee(w);
        w.ExecutionsPlaceForTests(1, new Vector3(0f, 0f, 2f), 0f);   // in the air: falls for a few ticks
        Idle(w);
        Assert.False(w.Players[1].Grounded);
        Tap(w, 1, kill + 2);
        Assert.Equal([(1, ExecutionRefusal.Airborne)], w.ExecutionsRefusedThisTick);   // refused before the choice (no NoFit, no NoOffer)
        Assert.Single(w.ExecutionsOffersOpen);   // and the offer is not spent
    }

    [Fact]
    public void PuppetNeverExecutes()
    {
        var w = Arena(Config(), (Tactical, Vector3.Zero), (Chaos, Near));
        int puppet = TfwContracts.PuppetIdFirst;
        w.Spawn(puppet, Tactical);
        w.ExecutionsPlaceForTests(puppet, new Vector3(3f, 0f, 0f), 0f);
        Step(w, id => id == puppet ? InputButtons.Execute : InputButtons.None);
        Assert.Contains((puppet, ExecutionRefusal.Puppet), w.ExecutionsRefusedThisTick);
    }

    [Fact]
    public void A_reaction_refuses_the_press()
    {
        // Lead decision 8: a tap while the killer is interrupted (or staggered, stunned, flinching) does nothing; the offer stays for a later tap inside the window.
        var w = Arena(Config(), (Tactical, Vector3.Zero), (Chaos, Near), (Tapper, new Vector3(0.9f, 1.6f, 0f)));
        uint kill = KillByMelee(w);
        for (int k = 0; k < 8 && w.Players[1].FlinchTicks == 0 && w.Players[1].Reaction == ReactionKind.None; k++) Step(w, id => id == 3 && k == 0 ? InputButtons.Melee : InputButtons.None);
        Assert.True(w.Players[1].FlinchTicks > 0 || w.Players[1].Reaction != ReactionKind.None);
        Tap(w, 1, w.Tick + 1);
        Assert.Equal([(1, ExecutionRefusal.Reaction)], w.ExecutionsRefusedThisTick);
        Assert.Single(w.ExecutionsOffersOpen);
        _ = kill;
    }

    [Fact]
    public void LockedInputHasNoButtonsAndKeepsView()
    {
        var (w, kill) = Killed();
        Started(w, kill);
        for (int k = 0; k < 20; k++)
        {
            w.Step(id => new PlayerInput(0, 1f, 1f, id == 1 ? 1.5f : MathF.PI, id == 1 ? InputButtons.Fire | InputButtons.Melee | InputButtons.Jump : InputButtons.None, 0.3f, w.Tick));
            var p = w.Players[1];
            Assert.Equal(0, p.MeleeStep);
            Assert.True(p.Grounded);
            Assert.Empty(w.LastShots);
            Assert.Equal(0.3f, p.Pitch, 3);   // the view's pitch stays free
        }
    }

    [Fact]
    public void PinFollowsTrackEveryTick()
    {
        // The slide case, and the run as a client decodes it from the Start: the server's executor stands where ExecutionTrack puts it, every locked tick.
        var w = Arena(Config(), (Tactical, Vector3.Zero), (Chaos, new Vector3(0f, 2.0f, 0f)));
        uint kill = KillByMelee(w);
        var run = Started(w, kill);
        Assert.True(ExecutionWire.TryDecode(ExecutionWire.Encode(ExecutionStart.Of(run)), out ExecutionStart start));
        var client = start.ToRun();
        var row = w.Config.Executions.AtWire(start.Clip)!;
        int locked = 0;
        while (w.ExecutionsLocked(1))
        {
            Step(w, _ => InputButtons.None);
            if (!w.ExecutionsLocked(1) && w.ExecutionsEndedThisTick.Count > 0 && w.ExecutionsEndedThisTick[0].Reason != ExecutionEndReason.Done) Assert.Fail("ended early");
            var pose = ExecutionTrack.Pose(client, row, w.Tick, w.Config.TickRate);
            Assert.Equal(pose.Feet, w.Players[1].Position);
            Assert.Equal(pose.Yaw, w.Players[1].Yaw);
            Assert.True(ExecutionTrack.LockedAt(client, row, w.Tick, w.Config.TickRate));
            locked++;
        }
        Assert.Equal((int)(ExecutionTrack.EndTick(client, row, w.Config.TickRate) - run.StartTick), locked);
    }

    [Fact]
    public void ReactionBlockedWhileLocked()
    {
        var w = Arena(Config(), (Tactical, Vector3.Zero), (Chaos, Near), (Tapper, new Vector3(0.9f, 1.6f, 0f)));
        uint kill = KillByMelee(w);
        Started(w, kill);
        for (int k = 0; k < 10; k++)
        {
            Step(w, id => id == 3 && k == 0 ? InputButtons.Melee : InputButtons.None);
            Assert.Equal(ReactionKind.None, w.Players[1].Reaction);
            Assert.Equal(0, w.Players[1].FlinchTicks);
        }
        Assert.True(w.Players[1].Health < 100f);
        Assert.True(w.ExecutionsLocked(1));
    }

    [Fact]
    public void EndsDoneAtEndEvent()
    {
        var (w, kill) = Killed();
        var run = Started(w, kill);
        var row = w.Config.Executions.AtWire(run.Wire)!;
        bool killSeen = false;
        var end = RunToEnd(w, each: () => killSeen |= w.ExecutionOf(1) is { KillDone: true });
        Assert.Equal(ExecutionEndReason.Done, end.Reason);
        Assert.Equal(ExecutionTrack.EndTick(run, row, 30), end.Tick);
        Assert.True(killSeen && end.Run.KillDone);
        Assert.False(w.ExecutionsLocked(1));
    }

    [Fact]
    public void EndsKilledWhenExecutorDies()
    {
        var w = Arena(Config(), (Tactical, Vector3.Zero), (Chaos, Near), (Chaos, new Vector3(0.9f, 1.6f, 0f)));
        uint kill = KillByMelee(w);
        Started(w, kill);
        ExecutionEnded? end = null;
        for (int k = 0; k < 12 && end is null; k++)
        {
            Step(w, id => id == 3 && k == 0 ? InputButtons.Melee : InputButtons.None);
            if (w.ExecutionsEndedThisTick.Count > 0) end = w.ExecutionsEndedThisTick[0];
        }
        Assert.Equal(ExecutionEndReason.Killed, end!.Value.Reason);
    }

    [Fact]
    public void EndsDisconnectWhenExecutorLeaves()
    {
        var (w, kill) = Killed();
        Started(w, kill);
        Idle(w, 3);
        w.Remove(1);
        Step(w, _ => InputButtons.None);
        Assert.Equal(ExecutionEndReason.Disconnect, Assert.Single(w.ExecutionsEndedThisTick).Reason);
        Assert.Empty(w.ExecutionsLive);
    }

    [Fact]
    public void EndsMatchEndOnRestart()
    {
        // A match with rules: the run is started while waiting; both sides being in begins the round (a respawn for everyone): MatchEnd.
        var c = Config();
        var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, 5f, 0f), MathF.PI)]) { Rules = new MatchRules() };
        w.Spawn(1, Tactical);
        var p = w.Players[1];
        w.ExecutionsStartForTests(new ExecutionRun(1, ExecutionTargetKind.Player, 2, c.Executions.WireIndexOf("C1.chainsword"), 1.3f, p.Position, p.Position, p.Yaw, 0, w.Tick, false));
        Step(w, _ => InputButtons.None);
        w.Spawn(2, Chaos);
        ExecutionEnded? end = null;
        for (int k = 0; k < 10 && end is null; k++)
        {
            Step(w, _ => InputButtons.None);
            if (w.ExecutionsEndedThisTick.Count > 0) end = w.ExecutionsEndedThisTick[0];
        }
        Assert.Equal(ExecutionEndReason.MatchEnd, end!.Value.Reason);
    }

    [Fact]
    public void EndsInvalidOnTeleport()
    {
        var (w, kill) = Killed();
        Started(w, kill);
        Idle(w, 3);
        w.ExecutionsPlaceForTests(1, new Vector3(20f, 0f, 0f), 0f);
        Step(w, _ => InputButtons.None);
        Assert.Equal(ExecutionEndReason.Invalid, Assert.Single(w.ExecutionsEndedThisTick).Reason);
    }

    [Fact]
    public void A_small_push_is_pinned_back()
    {
        var (w, kill) = Killed();
        var run = Started(w, kill);
        Idle(w, 3);
        w.ExecutionsPlaceForTests(1, w.Players[1].Position + new Vector3(0.3f, 0f, 0f), w.Players[1].Yaw);   // a shove after the pin
        Step(w, _ => InputButtons.None);
        Assert.True(w.ExecutionsLocked(1));
        var row = w.Config.Executions.AtWire(run.Wire)!;
        Assert.Equal(ExecutionTrack.Pose(run, row, w.Tick, 30).Feet, w.Players[1].Position);
    }

    [Fact]
    public void AbortAllEndsEveryRun()
    {
        var c = Config();
        var w = Arena(c, (Tactical, Vector3.Zero), (Chaos, new Vector3(5f, 0f, 0f)));
        int wire = c.Executions.WireIndexOf("C1.chainsword");
        foreach (int id in new[] { 1, 2 })
        {
            var p = w.Players[id];
            w.ExecutionsStartForTests(new ExecutionRun(id, ExecutionTargetKind.Player, 3 - id, wire, 1.3f, p.Position, p.Position, p.Yaw, 0, w.Tick, false));
        }
        Step(w, _ => InputButtons.None);
        Assert.Equal(2, w.ExecutionsStartedThisTick.Count);
        w.ExecutionsAbortAll(ExecutionEndReason.MatchEnd);
        Assert.Empty(w.ExecutionsLive);
        Step(w, _ => InputButtons.None);
        Assert.Equal([1, 2], w.ExecutionsEndedThisTick.Select(e => e.Run.Executor).Order());
        Assert.All(w.ExecutionsEndedThisTick, e => Assert.Equal(ExecutionEndReason.MatchEnd, e.Reason));
        Step(w, _ => InputButtons.None);
        Assert.Empty(w.ExecutionsEndedThisTick);   // reported once
    }

    [Fact]
    public void NoFlagNoState()
    {
        var (w, kill) = Killed(Config(extras: ContentExtras.None));
        for (int k = 0; k < 30; k++) Step(w, id => id == 1 && k % 2 == 0 ? InputButtons.Execute : InputButtons.None, kill + 1);
        Assert.Empty(w.ExecutionsOffersOpen);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Empty(w.ExecutionsRefusedThisTick);
        Assert.Empty(w.ExecutionsLive);
    }

    [Fact]
    public void An_empty_catalogue_refuses_before_anything()
    {
        var (w, kill) = Killed(Config(ExecutionRowSet.Empty));
        Tap(w, 1, kill + 1);
        Assert.Equal([(1, ExecutionRefusal.NoRows)], w.ExecutionsRefusedThisTick);
    }
}
