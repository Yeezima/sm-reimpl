using System.Numerics;
using SmNet.Extras.Executions;
using SmSim;
using SmSim.Extras.Executions;
using static SmNet.Tests.Extras.Executions.Acceptance.ExecutionFixtures;

namespace SmNet.Tests.Extras.Executions.Acceptance;

/// <summary>
/// Executions X8 drop 1, Classic PvP Extended: one test per Verify item of the owner-approved brief (2026-11-25), written from the brief and the lead's adjustments (slide cap 2.5 m, a press refused
/// during an interrupt, stun or stagger but not during a flinch), independent of the implementers' unit tests. Synthetic rows and levels only (<see cref="ExecutionFixtures"/>).
/// </summary>
public class ClassicExecutionAcceptanceTests
{
    // ---------------------------------------------------------------- the window

    [Fact]
    public void Window_WMinus1_Refused_W_Accepted_WPlus1_Refused()
    {
        int window = ExecutionRules.WindowTicks(Config());
        Assert.Equal(24, window);   // 0.8 s at 30 Hz
        // one tick before the kill: refused
        var (a, ka) = Killed();
        Tap(a, 1, ka - 1);
        Assert.Empty(a.ExecutionsStartedThisTick);
        // the kill tick itself and the last tick of the window (kill + W): accepted
        var (b, kb) = Killed();
        Tap(b, 1, kb);
        Assert.Single(b.ExecutionsStartedThisTick);
        var (c, kc) = Killed();
        Idle(c, window - 1);
        Tap(c, 1, kc + (uint)window);
        Assert.Single(c.ExecutionsStartedThisTick);
        // one tick past the window: refused, and the offer is no longer open for a later press either
        var (d, kd) = Killed();
        Idle(d, window);
        Tap(d, 1, kd + (uint)window + 1);
        Assert.Empty(d.ExecutionsStartedThisTick);
        Assert.False(d.ExecutionsLocked(1));
        // and a press made late in real time that claims an early view tick is clamped to MaxRewindTicks back
        var (e, ke) = Killed();
        Idle(e, 44);
        Tap(e, 1, ke);
        Assert.Empty(e.ExecutionsStartedThisTick);
    }

    [Fact]
    public void HeldPress_Refused()
    {
        var w = Arena(Config(), (Tactical, Vector3.Zero), (Chaos, Near));
        bool started = false;
        // Execute is down from the very first tick and stays down through the kill and into the window.
        for (int k = 0; k < 8; k++)
        {
            Step(w, id => id == 1 ? InputButtons.Execute | (k == 0 ? InputButtons.Melee : InputButtons.None) : InputButtons.None);
            started |= w.ExecutionsStartedThisTick.Count > 0;
        }
        Assert.NotEmpty(w.KillLog);
        Assert.False(started);
        Assert.False(w.ExecutionsLocked(1));
        Assert.Single(w.ExecutionsOffersOpen);   // the held press consumed nothing
        // control: releasing for a tick and pressing again inside the window does start it
        Step(w, _ => InputButtons.None);
        Tap(w, 1);
        Assert.Single(w.ExecutionsStartedThisTick);
    }

    [Fact]
    public void PreArmedPress_BeforeKill_Refused()
    {
        uint kill;
        { var (_, k) = Killed(); kill = k; }
        // a press edge one tick before the kill (it sees no offer) ...
        var w = Arena(Config(), (Tactical, Vector3.Zero), (Chaos, Near));
        bool started = false;
        for (int k = 0; k < 40 && w.Tick < kill + 6; k++)
        {
            bool press = w.Tick + 1 == kill - 1;
            Step(w, id => id == 1 ? (k == 0 ? InputButtons.Melee : InputButtons.None) | (press ? InputButtons.Execute : InputButtons.None) : InputButtons.None);
            started |= w.ExecutionsStartedThisTick.Count > 0;
        }
        Assert.False(started);
        Assert.False(w.ExecutionsLocked(1));
        Assert.Single(w.ExecutionsOffersOpen);
        // ... and holding the pre-armed key through the kill does not start it either
        var held = Arena(Config(), (Tactical, Vector3.Zero), (Chaos, Near));
        for (int k = 0; k < 40 && held.Tick < kill + 6; k++)
        {
            bool down = held.Tick + 1 >= kill - 1;
            Step(held, id => id == 1 ? (k == 0 ? InputButtons.Melee : InputButtons.None) | (down ? InputButtons.Execute : InputButtons.None) : InputButtons.None);
            started |= held.ExecutionsStartedThisTick.Count > 0;
        }
        Assert.False(started);
        // control: a fresh tap inside the window starts it
        Step(w, _ => InputButtons.None);
        Tap(w, 1);
        Assert.Single(w.ExecutionsStartedThisTick);
    }

    [Fact]
    public void OneFinisherPerCorpse()
    {
        var (w, kill) = Killed();
        Tap(w, 1, kill + 1);
        Assert.Single(w.ExecutionsStartedThisTick);
        Assert.Empty(w.ExecutionsOffersOpen);
        int starts = 1;
        // tap again, several times, while the move plays: refused (locked), never a second start
        for (int i = 0; i < 6; i++)
        {
            Step(w, _ => InputButtons.None);
            Tap(w, 1);
            starts += w.ExecutionsStartedThisTick.Count;
        }
        Assert.Equal(1, starts);
        // a try that fails also spends the corpse: a 4.2 m gap against a [1.25, 1.3] band is over the 2.5 m cap
        var far = Arena(Config(ExecutionRowSet.OfAll([ChaosSingleRow(1.3f)])) with { MeleeRange = 5f }, (Tactical, Vector3.Zero), (Chaos, new Vector3(0f, 4.2f, 0f)));
        uint k3 = KillByMelee(far);
        Tap(far, 1, k3 + 1);
        Assert.Empty(far.ExecutionsStartedThisTick);
        Assert.Contains((1, ExecutionRefusal.NoFit), far.ExecutionsRefusedThisTick);
        Step(far, _ => InputButtons.None);
        Tap(far, 1, k3 + 3);
        Assert.Contains((1, ExecutionRefusal.NoOffer), far.ExecutionsRefusedThisTick);
        Assert.Empty(far.ExecutionsStartedThisTick);
    }

    // ---------------------------------------------------------------- the move's effect

    [Fact]
    public void NoSecondKill_KillCountStaysOne()
    {
        var (w, kill) = Killed();
        int sequence = w.LastKillSequence, logged = w.KillLog.Count;
        Tap(w, 1, kill + 1);
        for (int i = 0; i < 100; i++)
        {
            Step(w, _ => InputButtons.None);
            Assert.DoesNotContain(w.LastKills, k => k.Killer == 1);
        }
        Assert.Empty(w.ExecutionsLive);
        Assert.Equal(sequence, w.LastKillSequence);
        Assert.Equal(logged, w.KillLog.Count);
        Assert.Equal(1, w.Scores[1].Kills);
        Assert.Equal(0, w.Scores[1].Deaths);
        Assert.Equal(1, w.Scores[2].Deaths);
        Assert.Equal(0, w.Scores[2].Kills);
    }

    [Fact]
    public void VictimRespawn_NotDelayed_BitForBit()
    {
        var (with, kw) = Killed();
        var (without, kn) = Killed();
        Assert.Equal(kw, kn);
        Tap(with, 1, kw + 1);
        Step(without, _ => InputButtons.None);
        Assert.Single(with.ExecutionsLive);
        bool respawned = false;
        for (int i = 0; i < 300; i++)
        {
            // the victim's whole state, tick for tick, with and without the finisher (alive flag, respawn timer, health, shield, position, everything)
            Assert.Equal(without.Players[2], with.Players[2]);
            respawned |= with.Players[2].Alive;
            Step(with, _ => InputButtons.None);
            Step(without, _ => InputButtons.None);
        }
        Assert.True(respawned, "the victim never respawned in 300 ticks (the comparison would be vacuous)");
    }

    [Fact]
    public void Nonlethal_DoesNotInterrupt()
    {
        var (w, kill) = Killed(more: (Tapper, new Vector3(0.9f, 1.6f, 0f)));
        Tap(w, 1, kill + 1);
        Assert.True(w.ExecutionsLocked(1));
        float before = w.Players[1].Health + w.Players[1].Shield;
        // the tapper's heavy strike (10 damage and an interrupt reaction) lands on the locked executor: damage taken, no reaction, the move goes on
        for (int k = 0; k < 12; k++)
        {
            Step(w, id => id == 3 && k == 0 ? InputButtons.MeleeHeavy : InputButtons.None);
            Assert.Equal(ReactionKind.None, w.Players[1].Reaction);
            Assert.True(w.ExecutionsLocked(1));
        }
        Assert.True(w.Players[1].Health + w.Players[1].Shield < before, "the executor takes normal damage");
        Assert.Empty(w.ExecutionsEndedThisTick);
        // it plays out to the Done end
        ExecutionEnded? ended = null;
        for (int k = 0; k < 120 && ended is null; k++) { Idle(w, 1); if (w.ExecutionsEndedThisTick.Count > 0) ended = w.ExecutionsEndedThisTick[0]; }
        Assert.Equal(ExecutionEndReason.Done, ended!.Value.Reason);
        // control: the same strike on the now-free executor does interrupt it, so the lock is what held the reaction back
        for (int k = 0; k < 40 && w.Players[1].Reaction == ReactionKind.None; k++) Step(w, id => id == 3 && k == 0 ? InputButtons.MeleeHeavy : InputButtons.None);
        Assert.Equal(ReactionKind.Interrupt, w.Players[1].Reaction);
    }

    [Fact]
    public void Lethal_EndsKilled_KillStands()
    {
        var (w, kill) = Killed(more: (Chaos, new Vector3(0.9f, 1.6f, 0f)));
        Tap(w, 1, kill + 1);
        Assert.True(w.ExecutionsLocked(1));
        ExecutionEnded? ended = null;
        for (int k = 0; k < 14 && ended is null; k++)
        {
            Step(w, id => id == 3 && k == 0 ? InputButtons.Melee : InputButtons.None);
            if (w.ExecutionsEndedThisTick.Count > 0) ended = w.ExecutionsEndedThisTick[0];
        }
        Assert.NotNull(ended);
        Assert.Equal(ExecutionEndReason.Killed, ended!.Value.Reason);
        Assert.Equal(1, ended.Value.Run.Executor);
        Assert.False(w.Players[1].Alive);
        Assert.False(w.ExecutionsLocked(1));
        Assert.Equal(1, w.Scores[1].Kills);   // the kill it made stands, once
        Assert.Equal(1, w.Scores[1].Deaths);
        Assert.Equal(1, w.Scores[3].Kills);
    }

    [Fact]
    public void NoHealArmourOrOvershieldInClassic()
    {
        var c = Config();
        var (w, kill) = Killed(config: c, more: (Tapper, new Vector3(0.9f, 1.6f, 0f)));
        Tap(w, 1, kill + 1);
        float max = c.ShieldMax;
        // the executor is struck during the move (normal damage); it is never healed, never given shield, and never above its shield maximum afterwards
        for (int k = 0; k < 10; k++) Step(w, id => id == 3 && k == 0 ? InputButtons.Melee : InputButtons.None);
        float total = w.Players[1].Health + w.Players[1].Shield;
        Assert.True(total < c.HealthMax + max, "the strike must have landed for this test to mean anything");
        for (int i = 0; i < 150; i++)
        {
            Step(w, _ => InputButtons.None);
            float now = w.Players[1].Health + w.Players[1].Shield;
            Assert.True(now <= total + 1e-3f, $"health + armour rose from {total} to {now} at tick {w.Tick}");
            Assert.InRange(w.Players[1].Shield, 0f, max);
            Assert.Equal(0f, ExecutionRules.Overshield(w.Players[1].Shield, max));
            total = now;
        }
        Assert.Empty(w.ExecutionsLive);   // the move completed (a Done end), and still no reward
    }

    [Fact]
    public void FinisherDealsNoDamageNoScore()
    {
        var (w, kill) = Killed();
        var scores = w.Scores.ToDictionary(kv => kv.Key, kv => kv.Value);
        var executor = w.Players[1];
        Tap(w, 1, kill + 1);
        for (int i = 0; i < 100; i++)
        {
            Assert.Empty(w.LastHits);
            Assert.Equal(executor.Health, w.Players[1].Health);
            Assert.Equal(executor.Shield, w.Players[1].Shield);
            Step(w, _ => InputButtons.None);
        }
        foreach (var (id, s) in scores) Assert.Equal(s, w.Scores[id]);
        Assert.Equal(0, w.ScoreOf(Team.None));
    }

    // ---------------------------------------------------------------- races and weapons

    [Fact]
    public void RaceSwap_ChaosKillerOnLoyalist()
    {
        var (w, kill) = Killed(killer: Chaos, victim: Tactical);
        Tap(w, 1, kill + 1);
        var run = Assert.Single(w.ExecutionsStartedThisTick);
        Assert.True(run.RaceSwapped);
        Assert.Equal("C1.chainsword", w.Config.Executions.AtWire(run.Wire)!.Id);
        // the swap survives the wire
        Assert.True(ExecutionWire.TryDecode(ExecutionWire.Encode(ExecutionStart.Of(run)), out ExecutionStart start));
        Assert.True(start.RaceSwapped);
        Assert.True(start.ToRun().RaceSwapped);
        // a Chaos victim takes the Chaos rows as themselves: no swap
        var (w2, k2) = Killed(killer: Tactical, victim: Chaos);
        Tap(w2, 1, k2 + 1);
        Assert.False(Assert.Single(w2.ExecutionsStartedThisTick).RaceSwapped);
        // an Ork Boy victim takes the Boy rows, and an Ork player never executes
        var (w3, k3) = Killed(killer: Tactical, victim: Ork);
        Tap(w3, 1, k3 + 1);
        Assert.Equal("O1.chainsword", w3.Config.Executions.AtWire(Assert.Single(w3.ExecutionsStartedThisTick).Wire)!.Id);
        var (w4, k4) = Killed(killer: Ork, victim: Tactical);
        Tap(w4, 1, k4 + 1);
        Assert.Empty(w4.ExecutionsStartedThisTick);
        Assert.Contains((1, ExecutionRefusal.Race), w4.ExecutionsRefusedThisTick);
    }

    [Fact]
    public void PowerSword_UsesChainswordRows()
    {
        Assert.Equal(ExecutionWeapons.Chainsword, ExecutionWeapons.FamilyOf("sm_mp_melee_powersword"));
        var (w, kill) = Killed(killer: PowerSword, victim: Chaos);
        Tap(w, 1, kill + 1);
        var run = Assert.Single(w.ExecutionsStartedThisTick);
        var row = w.Config.Executions.AtWire(run.Wire)!;
        Assert.Equal("C1.chainsword", row.Id);
        Assert.Equal(ExecutionWeapons.Chainsword, row.Weapon);
    }

    [Theory]
    [InlineData(Kicker)]
    [InlineData(Claw)]
    [InlineData(Spear)]
    public void Kick_Claw_Spear_NoExecution(int meleeClass)
    {
        var (w, kill) = Killed(killer: meleeClass, victim: Chaos);
        Tap(w, 1, kill + 1);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.False(w.ExecutionsLocked(1));
        Assert.Contains((1, ExecutionRefusal.NoFamily), w.ExecutionsRefusedThisTick);
        Assert.Equal(1, w.Scores[1].Kills);   // the normal kill stands
    }

    [Fact]
    public void Guns_NoExecution()
    {
        // a pistol kill (KillCause.Weapon) opens no offer, so a tap does nothing
        var w = Arena(Config(), (Tactical, Vector3.Zero), (Chaos, new Vector3(0f, 5f, 0f)));
        KillEvent? kill = null;
        for (int k = 0; k < 160 && kill is null; k++)
        {
            Step(w, id => id == 1 && k % 8 == 0 ? InputButtons.Fire : InputButtons.None);
            Assert.Empty(w.ExecutionsOfferedThisTick);
            if (w.LastKills.Count > 0) kill = w.LastKills[0];
        }
        Assert.Equal(KillCause.Weapon, kill!.Value.Cause);
        Tap(w, 1);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Contains((1, ExecutionRefusal.NoOffer), w.ExecutionsRefusedThisTick);
    }

    [Fact]
    public void Friendly_Kill_And_Other_Killers_Get_No_Offer()
    {
        // a friendly-fire kill opens nothing
        var rules = new MatchRules { FriendlyFire = true };
        var w = Arena(Config(), null, rules, (Tactical, Vector3.Zero), (Tactical, Near));
        KillByMelee(w);
        Assert.Equal(1, w.LastKills[0].Killer);
        Assert.Empty(w.ExecutionsOffersOpen);
        // a bystander's tap during another killer's window does nothing
        var (b, kb) = Killed(more: (Tactical, new Vector3(3f, 0f, 0f)));
        Tap(b, 3, kb + 1);
        Assert.Empty(b.ExecutionsStartedThisTick);
        Assert.Single(b.ExecutionsOffersOpen);
    }

    [Fact]
    public void Refusals_Airborne_AndInterrupt_Refused_ButAFlinchIsNot()
    {
        // airborne: a jump before the tap
        var (a, ka) = Killed(level: Flat());   // a real floor, so a player placed above it falls
        a.ExecutionsPlaceForTests(1, new Vector3(0f, 0f, 3f), 0f);   // the killer in the air (a fall: a jump, a hop and a knock-up look the same to the press)
        Idle(a, 3);
        Assert.False(a.Players[1].Grounded, "the killer must be falling for this test");
        Tap(a, 1, ka + 1);
        Assert.Empty(a.ExecutionsStartedThisTick);
        Assert.Contains((1, ExecutionRefusal.Airborne), a.ExecutionsRefusedThisTick);
        // an interrupt, a stagger or a stun refuses the press (lead decision 8)
        foreach (var (striker, kind) in new[] { (Tapper, ReactionKind.Interrupt), (Staggerer, ReactionKind.Stagger), (Stunner, ReactionKind.Stun) })
        {
            var (w, _) = Killed(more: (striker, new Vector3(0.9f, 1.6f, 0f)));
            Step(w, id => id == 3 ? InputButtons.MeleeHeavy : InputButtons.None);
            for (int k = 0; k < 10 && w.Players[1].Reaction == ReactionKind.None; k++) Step(w, _ => InputButtons.None);
            Assert.Equal(kind, w.Players[1].Reaction);
            Tap(w, 1);
            Assert.Empty(w.ExecutionsStartedThisTick);
            Assert.Contains((1, ExecutionRefusal.Reaction), w.ExecutionsRefusedThisTick);
        }
        // a flinch (a light hit with no reaction: aim sway only, Reaction stays None) does not refuse: the press starts (lead 40)
        var (f, kf) = Killed(more: (Flincher, new Vector3(0.9f, 1.6f, 0f)));
        Step(f, id => id == 3 ? InputButtons.Melee : InputButtons.None);
        for (int k = 0; k < 8 && f.Players[1].FlinchTicks == 0; k++) Step(f, _ => InputButtons.None);
        Assert.True(f.Players[1].FlinchTicks > 0, "the light hit did not flinch the killer (the test needs it flinching)");
        Assert.Equal(ReactionKind.None, f.Players[1].Reaction);
        Tap(f, 1, kf + 1);
        Assert.Single(f.ExecutionsStartedThisTick);
    }

    // ---------------------------------------------------------------- terrain

    static readonly SimConfig SingleConfig = Config(ExecutionRowSet.OfAll([ChaosSingleRow(1.3f)]));
    const string Sword = "sm_mp_melee_chainsword";

    static (PlayerState Attacker, PlayerState Corpse) Pair(float corpseY, float corpseZ = 0f, float attackerZ = 0f, float attackerY = 0f) =>
        (new PlayerState(1, new Vector3(0f, attackerY, attackerZ), Vector3.Zero, 0f, true) { Alive = true, Team = Team.SpaceMarines },
         new PlayerState(2, new Vector3(0f, corpseY, corpseZ), Vector3.Zero, MathF.PI, true) { Alive = true, Team = Team.Chaos, Class = Chaos });

    static ExecutionAlignResult Choose(CollisionWorld level, float corpseY, float corpseZ = 0f, float attackerZ = 0f, float attackerY = 0f, float maxSlide = ExecutionRules.ClassicMaxSlide)
    {
        var (a, v) = Pair(corpseY, corpseZ, attackerZ, attackerY);
        return ExecutionChoice.ChooseClassic(a, "tactical", Sword, v, SingleConfig.Executions.Rows, SingleConfig, level, 1, null, maxSlide);
    }

    static void Refused(ExecutionAlignResult r, params ExecutionAlignFailure[] anyOf)
    {
        Assert.False(r.Ok, "the slide should have been refused");
        Assert.Contains(r.Failure, anyOf);
    }

    [Fact]
    public void Terrain_Flat_Fits_Wall_Gap_Ledge_Step_Slope_KillVolume_SpawnBlocker_RefuseCleanly()
    {
        // the control: the corpse 3.0 m ahead on the flat, a 1.7 m slide
        var ok = Choose(Flat(), 3.0f);
        Assert.True(ok.Ok, ok.Failure.ToString());
        Assert.InRange(ok.Slide, 1.6f, 1.8f);
        // wall across the slide
        Refused(Choose(Walled(), 3.0f), ExecutionAlignFailure.Blocked, ExecutionAlignFailure.NoSight, ExecutionAlignFailure.Step);
        // gap in the floor under the slide
        Refused(Choose(Gapped(), 3.0f), ExecutionAlignFailure.Gap, ExecutionAlignFailure.Blocked);
        // a ledge: the corpse on lower ground (1 m down) and the floor dropping away on the way
        Refused(Choose(Stepped(0.9f, -1.0f), 3.0f, corpseZ: -1.0f), ExecutionAlignFailure.Step, ExecutionAlignFailure.Gap);
        // a high step (0.6 m, over the 0.4 m step height) with the corpse on top
        Refused(Choose(Stepped(0.9f, 0.6f), 3.0f, corpseZ: 0.6f), ExecutionAlignFailure.Step, ExecutionAlignFailure.Blocked);
        // a steep slope (45 degrees): the corpse 3 m up the ramp
        Refused(Choose(Sloped(0f, 45f), 3.0f, corpseZ: SlopeZ(3.0f, 0f, 45f)), ExecutionAlignFailure.Step, ExecutionAlignFailure.Slope, ExecutionAlignFailure.Blocked);
        // a kill volume across the slide
        Refused(Choose(KillVolumed(), 3.0f), ExecutionAlignFailure.KillVolume, ExecutionAlignFailure.Blocked);
        // the enemy side's spawn-room blocker across the slide (it keeps Space Marines out)
        Refused(Choose(Blockered(Team.Chaos), 3.0f), ExecutionAlignFailure.SpawnBlocker, ExecutionAlignFailure.Blocked);
        // the own side's blocker does not stop its own side
        Assert.True(Choose(Blockered(Team.SpaceMarines), 3.0f).Ok);
    }

    [Fact]
    public void Terrain_LowStep_And_GentleSlope_Fit()
    {
        // a 0.3 m step (under the 0.4 m step height) and a 6 degree ramp (the corpse 0.32 m above the killer) are walkable: the move plays
        var step = Choose(Stepped(0.9f, 0.3f), 3.0f, corpseZ: 0.3f);
        Assert.True(step.Ok, "a 0.3 m step must fit: " + step.Failure);
        var ramp = Choose(Sloped(0f, 6f), 3.0f, corpseZ: SlopeZ(3.0f, 0f, 6f));
        Assert.True(ramp.Ok, "a 6 degree slope must fit: " + ramp.Failure);
    }

    [Fact]
    public void Terrain_LiveRefusalsLeaveTheNormalKillStanding()
    {
        // the pair 3.0 m apart (a lunge-range kill); the move cannot play: nothing starts, nothing moves, the kill stands
        (string Name, CollisionWorld Level, float VictimZ)[] cases =
        [
            ("gap", Gapped(), 0f),
            ("kill volume", KillVolumed(), 0f),
            ("enemy spawn blocker", Blockered(Team.Chaos), 0f),
            ("high step", Stepped(0.9f, 0.6f), 0.6f),
            ("ledge", Stepped(0.9f, -1.0f), -1.0f),
        ];
        foreach (var (name, level, z) in cases)
        {
            var w = Arena(SingleConfig, level, new MatchRules { CountdownTime = 0f, TimeLimit = 0f }, (Tactical, Vector3.Zero), (Chaos, new Vector3(0f, 3.0f, z)));   // match rules give the players their sides (a spawn blocker keeps one side out)
            uint kill = KillByMelee(w);
            var before = w.Players[1].Position;
            Tap(w, 1, kill + 1);
            Assert.True(w.ExecutionsStartedThisTick.Count == 0, name + ": a move started");
            Assert.False(w.ExecutionsLocked(1), name);
            Assert.Contains((1, ExecutionRefusal.NoFit), w.ExecutionsRefusedThisTick);
            Assert.Equal(1, w.Scores[1].Kills);
            Assert.InRange(Vector3.Distance(before, w.Players[1].Position), 0f, 0.2f);
        }
    }

    [Fact]
    public void Slide_2p4m_Corridor_Fits_AndWallInPathRefuses()
    {
        // a 2 m wide corridor (walls at x = -1 and x = +1, 3 m tall): the corpse 3.7 m ahead means a 2.4 m slide, inside the Classic 2.5 m cap
        var corridor = Level(
            [.. Box(new(-1.5f, -5f, 0f), new(-1f, 10f, 3f)), .. Box(new(1f, -5f, 0f), new(1.5f, 10f, 3f))]);
        var r = Choose(corridor, 3.7f);
        Assert.True(r.Ok, r.Failure.ToString());
        Assert.InRange(r.Slide, 2.35f, 2.45f);
        Assert.InRange(r.SlideTicks, 12, 14);   // 6 m/s on 30 ticks: about 0.4 s
        // Warfront's 1 m cap would refuse the same gap
        Assert.Equal(ExecutionAlignFailure.TooFar, Choose(corridor, 3.7f, maxSlide: ExecutionAlign.MaxSlide).Failure);
        // a waist-high wall across the corridor in the slide path refuses it
        var blocked = Level(
            [.. Box(new(-1.5f, -5f, 0f), new(-1f, 10f, 3f)), .. Box(new(1f, -5f, 0f), new(1.5f, 10f, 3f)), .. Box(new(-1f, 1.1f, 0f), new(1f, 1.4f, 1f))]);
        Refused(Choose(blocked, 3.7f), ExecutionAlignFailure.Blocked, ExecutionAlignFailure.NoSight, ExecutionAlignFailure.Step);
        // and the slide cap is a hard 2.5 m: a corpse 4.2 m ahead (2.9 m slide) is refused with TooFar
        Assert.Equal(ExecutionAlignFailure.TooFar, Choose(Flat(), 4.2f).Failure);
    }

    // ---------------------------------------------------------------- determinism

    [Fact]
    public void DeterministicPick_SameSeedSameRow()
    {
        var rows = ExecutionRowSet.OfAll([ChaosRow(), ChaosAltRow()]);
        var cfg = Config(rows);
        var (a, v) = Pair(2.4f);
        var seen = new HashSet<string>();
        for (uint tick = 0; tick < 96; tick++)
        {
            ulong seed = ExecutionChoice.Seed(tick, 1, 2);
            var first = ExecutionChoice.ChooseClassic(a, "tactical", Sword, v, rows.Rows, cfg, Flat(), seed, null, ExecutionRules.ClassicMaxSlide);
            var again = ExecutionChoice.ChooseClassic(a, "tactical", Sword, v, rows.Rows, cfg, Flat(), seed, null, ExecutionRules.ClassicMaxSlide);
            Assert.True(first.Ok);
            Assert.Equal((first.Row!.Id, first.Variant, first.Distance, first.Slide, first.SlideTicks), (again.Row!.Id, again.Variant, again.Distance, again.Slide, again.SlideTicks));
            seen.Add(first.Row.Id);
        }
        Assert.Equal(["C1.chainsword", "R1.chainsword"], seen.Order());   // the seed really chooses among both aligned rows
        // two whole worlds with the same history pick the same run, then stay equal tick for tick
        var (w1, k1) = Killed(config: cfg);
        var (w2, k2) = Killed(config: cfg);
        Tap(w1, 1, k1 + 1);
        Tap(w2, 1, k2 + 1);
        Assert.Equal(Assert.Single(w1.ExecutionsStartedThisTick), Assert.Single(w2.ExecutionsStartedThisTick));
        for (int i = 0; i < 80; i++)
        {
            Idle(w1, 1);
            Idle(w2, 1);
            Assert.Equal(w1.Players[1], w2.Players[1]);
            Assert.Equal(w1.Players[2], w2.Players[2]);
        }
    }

    // ---------------------------------------------------------------- ends

    [Fact]
    public void Disconnect_MidMove_EndsDisconnect_ClientCleanupMessage()
    {
        var (w, kill) = Killed();
        Tap(w, 1, kill + 1);
        Idle(w, 10);
        Assert.True(w.ExecutionsLocked(1));
        w.Remove(1);   // the executor leaves mid-move
        Idle(w, 1);
        var ended = Assert.Single(w.ExecutionsEndedThisTick);
        Assert.Equal((1, ExecutionEndReason.Disconnect), (ended.Run.Executor, ended.Reason));
        Assert.Empty(w.ExecutionsLive);
        // the cleanup message the other clients get is a valid End carrying that reason
        var bytes = ExecutionWire.Encode(new ExecutionEnd(ended.Tick, (ushort)ended.Run.Executor, ended.Reason));
        Assert.True(ExecutionWire.TryDecode(bytes, out ExecutionEnd end));
        Assert.Equal((ended.Tick, (ushort)1, ExecutionEndReason.Disconnect), (end.Tick, end.Executor, end.Reason));
    }

    [Fact]
    public void Disconnect_MidMove_OverTheNetwork_SendsEndToTheOthers()
    {
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(2), TimeSpan.Zero, 0), seed: 5, clients: 2, Config());
        h.Run(TimeSpan.FromSeconds(1.5));
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        foreach (var c in h.Clients) c.TakeExtras();
        var world = h.Server.World;
        int id = h.Clients[0].PlayerId;
        var p = world.Players[id];
        world.ExecutionsStartForTests(new ExecutionRun(id, ExecutionTargetKind.Player, h.Clients[1].PlayerId, world.Config.Executions.WireIndexOf("C1.chainsword"), 1.3f, p.Position, p.Position, p.Yaw, 0, world.Tick + 1, false));
        h.Run(TimeSpan.FromMilliseconds(200));
        h.Clients[0].Leave();
        h.Run(TimeSpan.FromMilliseconds(400));
        var ends = h.Clients[1].TakeExtras().Where(s => s.Tag == ExecutionWire.Tag && ExecutionWire.KindOf(s.Payload) == ExecutionWire.EndKind)
            .Select(s => { Assert.True(ExecutionWire.TryDecode(s.Payload, out ExecutionEnd e)); return e; }).ToList();
        Assert.NotEmpty(ends);
        Assert.All(ends, e => Assert.Equal((ushort)id, e.Executor));
        Assert.All(ends, e => Assert.Equal(ExecutionEndReason.Disconnect, e.Reason));
    }

    [Fact]
    public void MatchEnd_MidMove_EndsMatchEnd()
    {
        // a time limit that falls in the middle of the move
        var rules = new MatchRules { CountdownTime = 0f, TimeLimit = 1.5f, RestartDelay = 30f };
        var (w, kill) = Killed(rules: rules);
        Tap(w, 1, kill + 1);
        ExecutionEnded? ended = null;
        for (int i = 0; i < 150 && ended is null; i++) { Idle(w, 1); if (w.ExecutionsEndedThisTick.Count > 0) ended = w.ExecutionsEndedThisTick[0]; }
        Assert.NotNull(ended);
        Assert.Equal(ExecutionEndReason.MatchEnd, ended!.Value.Reason);
        Assert.Equal(MatchState.Ended, w.State);
        // a restart of the round ends a move too
        var (restart, kr) = Killed(rules: new MatchRules { CountdownTime = 0f, TimeLimit = 0f });
        Tap(restart, 1, kr + 1);
        Assert.Single(restart.ExecutionsStartedThisTick);
        Idle(restart, 5);
        Assert.True(restart.DevRestartMatch());
        Idle(restart, 1);
        Assert.Equal(ExecutionEndReason.MatchEnd, Assert.Single(restart.ExecutionsEndedThisTick).Reason);
    }
}
