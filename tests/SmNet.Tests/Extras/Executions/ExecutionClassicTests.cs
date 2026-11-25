using System.Numerics;
using SmSim;
using SmSim.Extras.Executions;
using static SmNet.Tests.Extras.Executions.ExecutionTestKit;

namespace SmNet.Tests.Extras.Executions;

/// <summary>Executions X3, Classic PvP Extended: the window after a lethal melee kill, the offer, the finisher's (lack of) effect, the refusals, the race swap and the bystanders. Synthetic rows, no level.</summary>
public class ExecutionClassicTests
{
    static readonly Vector3 Near = new(0f, 1.2f, 0f);

    static (World W, uint Kill) Killed(int killerClass = Tactical, int victimClass = Chaos, SimConfig? c = null, params (int Class, Vector3 At)[] more)
    {
        var w = Arena(c ?? Config(), [(killerClass, Vector3.Zero), (victimClass, Near), .. more]);
        uint kill = KillByMelee(w);
        Assert.Equal(KillCause.Melee, w.LastKills[0].Cause);
        return (w, kill);
    }

    static void Idle(World w, int ticks) { for (int i = 0; i < ticks; i++) Step(w, _ => InputButtons.None); }

    [Fact]
    public void A_tap_inside_the_window_starts_a_finisher_on_the_corpse()
    {
        var (w, kill) = Killed();
        Assert.Single(w.ExecutionsOfferedThisTick);
        Tap(w, 1, kill + 1);
        var run = Assert.Single(w.ExecutionsStartedThisTick);
        Assert.Equal((1, ExecutionTargetKind.Player, 2), (run.Executor, run.TargetKind, run.Target));
        Assert.Equal(w.Config.Executions.WireIndexOf("C1.chainsword"), run.Wire);
        Assert.False(run.RaceSwapped);
        Assert.True(w.ExecutionsLocked(1));
    }

    [Fact]
    public void WindowBoundaryMinusOneTickRefused()
    {
        var (w, kill) = Killed();
        Tap(w, 1, kill - 1);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Contains((1, ExecutionRefusal.NoOffer), w.ExecutionsRefusedThisTick);
    }

    [Fact]
    public void WindowLastTickAccepted()
    {
        var (w, kill) = Killed();
        Idle(w, 23);
        Tap(w, 1, kill + 24);
        Assert.Single(w.ExecutionsStartedThisTick);
    }

    [Fact]
    public void WindowPlusOneTickRefused()
    {
        var (w, kill) = Killed();
        Idle(w, 24);
        Tap(w, 1, kill + 25);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Contains((1, ExecutionRefusal.NoOffer), w.ExecutionsRefusedThisTick);
    }

    [Fact]
    public void PressTickClampedToMaxRewind()
    {
        // A press 30 ticks after the kill that claims it saw the kill tick: clamped to tick - 15, outside the 24-tick window... the clamp alone keeps it in.
        var (w, kill) = Killed();
        Idle(w, 29);
        Tap(w, 1, kill);   // the view tick is clamped to (now - 15) = kill + 15: in the window
        Assert.Single(w.ExecutionsStartedThisTick);
        var (w2, kill2) = Killed();
        Idle(w2, 44);
        Tap(w2, 1, kill2);   // clamped to kill + 30: out
        Assert.Empty(w2.ExecutionsStartedThisTick);
    }

    [Fact]
    public void OnlyMeleeKillsOffer()
    {
        // A pistol kill (KillCause.Weapon): no offer. World, Thrown, Perk and JumpPack kills take the same filter (cause != Melee).
        var w = Arena(Config(), (Tactical, Vector3.Zero), (Chaos, new Vector3(0f, 5f, 0f)));
        KillEvent? kill = null;
        for (int k = 0; k < 120 && kill is null; k++)
        {
            Step(w, id => id == 1 && k % 8 == 0 ? InputButtons.Fire : InputButtons.None);
            if (w.LastKills.Count > 0) kill = w.LastKills[0];
            Assert.Empty(w.ExecutionsOfferedThisTick);
        }
        Assert.Equal(KillCause.Weapon, kill!.Value.Cause);
        Assert.Empty(w.ExecutionsOffersOpen);

    }

    [Fact]
    public void FriendlyKillNeverOffers()
    {
        var c = Config();
        var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(Near, MathF.PI)]) { Rules = new MatchRules { FriendlyFire = true } };
        w.Spawn(1, Tactical);
        w.Spawn(2, Tactical);
        KillByMelee(w);
        Assert.Equal(1, w.LastKills[0].Killer);
        Assert.Empty(w.ExecutionsOffersOpen);
    }

    [Fact]
    public void OfferOnlyForKiller()
    {
        var (w, kill) = Killed(more: (Tactical, new Vector3(3f, 0f, 0f)));
        Tap(w, 3, kill + 1);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Contains((3, ExecutionRefusal.NoOffer), w.ExecutionsRefusedThisTick);
        Assert.Single(w.ExecutionsOffersOpen);
    }

    [Fact]
    public void OneFinisherPerCorpse()
    {
        var (w, kill) = Killed();
        Tap(w, 1, kill + 1);
        Assert.Single(w.ExecutionsStartedThisTick);
        Assert.Empty(w.ExecutionsOffersOpen);
        // A failed try spends it too: a corpse no move fits (4.2 m away, a band of [1.25, 1.3]: over the 2.5 m Classic slide).
        var far = Arena(Config(ExecutionRowSet.OfAll([Row("C1.chainsword", gap: 1.3f, near: 1.25f)])) with { MeleeRange = 5f }, (Tactical, Vector3.Zero), (Chaos, new Vector3(0f, 4.2f, 0f)));
        uint k3 = KillByMelee(far);
        Tap(far, 1, k3 + 1);
        Assert.Empty(far.ExecutionsStartedThisTick);
        Assert.Contains((1, ExecutionRefusal.NoFit), far.ExecutionsRefusedThisTick);
        Step(far, _ => InputButtons.None);
        Tap(far, 1, k3 + 3);
        Assert.Contains((1, ExecutionRefusal.NoOffer), far.ExecutionsRefusedThisTick);
    }

    [Fact]
    public void NoSecondKillAndScoreUnchanged()
    {
        var (w, kill) = Killed();
        int sequence = w.LastKillSequence;
        Tap(w, 1, kill + 1);
        for (int i = 0; i < 80; i++)
        {
            Step(w, _ => InputButtons.None);
            Assert.DoesNotContain(w.LastKills, k => k.Killer == 1);
        }
        Assert.Empty(w.ExecutionsLive);   // ended
        Assert.Equal(sequence, w.LastKillSequence);
        Assert.Equal(1, w.Scores[1].Kills);
        Assert.Equal(1, w.Scores[2].Deaths);
    }

    [Fact]
    public void VictimRespawnNotDelayed()
    {
        var (a, ka) = Killed();
        var (b, kb) = Killed();
        Assert.Equal(ka, kb);
        Tap(a, 1, ka + 1);
        Step(b, _ => InputButtons.None);
        Assert.Single(a.ExecutionsLive);
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(b.Players[2].RespawnLeft, a.Players[2].RespawnLeft);
            Assert.Equal(b.Players[2].Alive, a.Players[2].Alive);
            Step(a, _ => InputButtons.None);
            Step(b, _ => InputButtons.None);
        }
    }

    [Fact]
    public void NonlethalHitDoesNotInterrupt()
    {
        // Player 3 (a tapper: 10 damage) strikes the executor during the move: no reaction, the run goes on.
        var (w, kill) = Killed(more: (Tapper, new Vector3(0.9f, 1.6f, 0f)));
        Tap(w, 1, kill + 1);
        var run = Assert.Single(w.ExecutionsLive);
        for (int k = 0; k < 10; k++) Step(w, id => id == 3 && k == 0 ? InputButtons.Melee : InputButtons.None);
        Assert.True(w.Players[1].Health < 100f);
        Assert.Equal(ReactionKind.None, w.Players[1].Reaction);
        Assert.True(w.ExecutionsLocked(1));
        Assert.Empty(w.ExecutionsEndedThisTick);
        _ = run;
    }

    [Fact]
    public void LethalHitEndsKilledKillStands()
    {
        var (w, kill) = Killed(more: (Chaos, new Vector3(0.9f, 1.6f, 0f)));
        Tap(w, 1, kill + 1);
        Assert.True(w.ExecutionsLocked(1));
        ExecutionEnded? ended = null;
        for (int k = 0; k < 12 && ended is null; k++)
        {
            Step(w, id => id == 3 && k == 0 ? InputButtons.Melee : InputButtons.None);
            if (w.ExecutionsEndedThisTick.Count > 0) ended = w.ExecutionsEndedThisTick[0];
        }
        Assert.Equal(ExecutionEndReason.Killed, ended!.Value.Reason);
        Assert.False(w.Players[1].Alive);
        Assert.Equal(1, w.Scores[1].Kills);   // the kill it made stands, once
        Assert.Equal(1, w.Scores[3].Kills);
    }

    [Fact]
    public void RaceSwapChaosOnLoyalist()
    {
        var (w, kill) = Killed(killerClass: Chaos, victimClass: Tactical);
        Tap(w, 1, kill + 1);
        var run = Assert.Single(w.ExecutionsStartedThisTick);
        Assert.True(run.RaceSwapped);
        Assert.StartsWith("C", w.Config.Executions.AtWire(run.Wire)!.Id);
    }

    [Fact]
    public void OrkPlayerVictimUsesBoyRows()
    {
        var (w, kill) = Killed(killerClass: Tactical, victimClass: Ork);
        Tap(w, 1, kill + 1);
        var run = Assert.Single(w.ExecutionsStartedThisTick);
        Assert.Equal("O1.chainsword", w.Config.Executions.AtWire(run.Wire)!.Id);
        Assert.False(run.RaceSwapped);
    }

    [Fact]
    public void OrkPlayerCannotExecute()
    {
        var (w, kill) = Killed(killerClass: Ork, victimClass: Tactical);
        Tap(w, 1, kill + 1);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Contains((1, ExecutionRefusal.Race), w.ExecutionsRefusedThisTick);
    }

    [Fact]
    public void A_kick_has_no_family()
    {
        var (w, kill) = Killed(killerClass: Kicker, victimClass: Chaos);
        Tap(w, 1, kill + 1);
        Assert.Contains((1, ExecutionRefusal.NoFamily), w.ExecutionsRefusedThisTick);
    }

    [Fact]
    public void BlockedSlideMeansNormalKillStands()
    {
        // The only row needs 1.25 to 1.3 m: a corpse 4.2 m away needs a 2.9 m slide (over the 2.5 m Classic cap): the normal kill stands, nothing moves.
        var c = Config(ExecutionRowSet.OfAll([Row("C1.chainsword", gap: 1.3f, near: 1.25f)])) with { MeleeRange = 5f };
        var w = Arena(c, (Tactical, Vector3.Zero), (Chaos, new Vector3(0f, 4.2f, 0f)));
        uint kill = KillByMelee(w);
        var before = w.Players[1].Position;
        Tap(w, 1, kill + 1);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.False(w.ExecutionsLocked(1));
        Assert.Equal(1, w.Scores[1].Kills);
        Assert.InRange(Vector3.Distance(before, w.Players[1].Position), 0f, 0.2f);
    }

    [Fact]
    public void A_slide_into_the_band_plays_first()
    {
        // The corpse at 2.0 m and a band of [1.0, 1.3]: a 0.7 m slide toward it, then the clip.
        var w = Arena(Config(), (Tactical, Vector3.Zero), (Chaos, new Vector3(0f, 2.0f, 0f)));
        uint kill = KillByMelee(w);
        Tap(w, 1, kill + 1);
        var run = Assert.Single(w.ExecutionsStartedThisTick);
        Assert.True(run.SlideTicks > 0);
        Assert.InRange(Vector3.Distance(run.SlideFrom, run.Origin), 0.6f, 0.8f);
    }

    [Fact]
    public void BystanderBlocksSlide()
    {
        // The same 0.7 m slide with a third player standing on its line: refused (the slide never pushes through a player).
        // A narrow 20-degree cone so the swing leaves the bystander (29 degrees off) alive.
        var w = Arena(Config() with { MeleeConeDegrees = 20f }, (Tactical, Vector3.Zero), (Chaos, new Vector3(0f, 2.0f, 0f)), (Tactical, new Vector3(0.45f, 0.8f, 0f)));
        uint kill = KillByMelee(w);
        Assert.Equal(2, Assert.Single(w.LastKills).Victim);
        Assert.True(w.Players[3].Alive);
        Tap(w, 1, kill + 1);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Contains((1, ExecutionRefusal.NoFit), w.ExecutionsRefusedThisTick);
    }

    [Fact]
    public void Determinism_two_worlds_pick_the_same_run()
    {
        var (a, ka) = Killed();
        var (b, kb) = Killed();
        Tap(a, 1, ka + 1);
        Tap(b, 1, kb + 1);
        Assert.Equal(Assert.Single(a.ExecutionsStartedThisTick), Assert.Single(b.ExecutionsStartedThisTick));
        for (int i = 0; i < 70; i++)
        {
            Step(a, _ => InputButtons.None);
            Step(b, _ => InputButtons.None);
            Assert.Equal(a.Players[1], b.Players[1]);
        }
    }

    // ---- lead 40: Classic's 2.5 m forward slide, through the same terrain checks ----

    static readonly ExecutionRowSet Single = ExecutionRowSet.OfAll([Row("C1.chainsword", gap: 1.3f, near: 1.3f)]);   // a single-clip row, as the real C1 to C3

    static (PlayerState Attacker, PlayerState Corpse) Pair(float gap) =>
        (new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Alive = true, Team = Team.SpaceMarines },
         new PlayerState(2, new Vector3(0f, gap, 0f), Vector3.Zero, MathF.PI, true) { Alive = true, Team = Team.Chaos, Class = Chaos });

    [Fact]
    public void A_2p4_m_slide_down_a_corridor_fits()
    {
        // A 2 m wide corridor (walls at x = +-1): the corpse 3.7 m ahead, a 2.4 m slide along it.
        var corridor = Boxes((new(-1.5f, -5f, 0f), new(-1f, 10f, 3f)), (new(1f, -5f, 0f), new(1.5f, 10f, 3f)));
        var c = Config(Single);
        var (a, v) = Pair(3.7f);
        var r = ExecutionChoice.ChooseClassic(a, "tactical", "sm_mp_melee_chainsword", v, Single.Rows, c, corridor, 1, null, ExecutionRules.ClassicMaxSlide);
        Assert.True(r.Ok, r.Failure.ToString());
        Assert.InRange(r.Slide, 2.35f, 2.45f);
        Assert.InRange(r.SlideTicks, 12, 14);   // 6 m/s: about 0.4 s
        // Warfront's default cap refuses the same gap.
        Assert.Equal(ExecutionAlignFailure.TooFar, ExecutionChoice.ChooseClassic(a, "tactical", "sm_mp_melee_chainsword", v, Single.Rows, c, corridor, 1).Failure);
    }

    [Fact]
    public void A_wall_in_the_slide_path_refuses()
    {
        // A waist-high crate across the corridor 1.2 m ahead: the slide is never pulled through it.
        var blocked = Boxes((new(-1.5f, -5f, 0f), new(-1f, 10f, 3f)), (new(1f, -5f, 0f), new(1.5f, 10f, 3f)), (new(-1f, 1.1f, 0f), new(1f, 1.4f, 1.0f)));
        var (a, v) = Pair(3.7f);
        var r = ExecutionChoice.ChooseClassic(a, "tactical", "sm_mp_melee_chainsword", v, Single.Rows, Config(Single), blocked, 1, null, ExecutionRules.ClassicMaxSlide);
        Assert.False(r.Ok);
        Assert.Contains(r.Failure, new[] { ExecutionAlignFailure.Blocked, ExecutionAlignFailure.Step, ExecutionAlignFailure.NoSight });
        // A gap in the floor on the path refuses too (the floor is cut between 1.5 and 2.5 m ahead).
        var gapped = GappedFloor(1.5f, 2.5f);
        Assert.False(ExecutionChoice.ChooseClassic(a, "tactical", "sm_mp_melee_chainsword", v, Single.Rows, Config(Single), gapped, 1, null, ExecutionRules.ClassicMaxSlide).Ok);
    }

    [Fact]
    public void A_live_kill_at_a_normal_melee_distance_plays_with_the_classic_slide()
    {
        // The corpse 3.0 m away (a lunge-range kill): a 1.7 m slide, then the clip.
        var w = Arena(Config(Single), Flat(), (Tactical, Vector3.Zero), (Chaos, new Vector3(0f, 3.0f, 0f)));
        uint kill = KillByMelee(w);
        Tap(w, 1, kill + 1);
        var run = Assert.Single(w.ExecutionsStartedThisTick);
        Assert.InRange(Vector3.Distance(run.SlideFrom, run.Origin), 1.0f, 2.5f);
    }

    static CollisionWorld GappedFloor(float from, float to)
    {
        Vector3[] v = [new(-100, -100, 0), new(100, -100, 0), new(100, from, 0), new(-100, from, 0), new(-100, to, 0), new(100, to, 0), new(100, 100, 0), new(-100, 100, 0)];
        var m = new StaticMesh(v, [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]);
        return new CollisionWorld(m, m);
    }
}