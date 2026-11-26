using System.Numerics;
using SmClient.Customizer.Model;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// camera/melee-orbit (owner 2026-11-25): the shared melee mode's enter and exit rules (<see cref="MeleeMode"/>), the run-where-you-steer body yaw
/// (<see cref="MeleeOrbitSteer"/>), and that the simulation steps the sent input the same on the server and in the prediction. Synthetic data only.
/// </summary>
public class MeleeModeTests
{
    const float Tick = 1f / 30f;
    const float Deg = MathF.PI / 180f;

    static MeleeMode.Inputs In(bool strike = false, bool fire = false, bool aim = false, bool reload = false, bool alive = true, bool excluded = false) =>
        new(alive, strike, fire, aim, reload, excluded);

    static MeleeMode Entered()
    {
        var m = new MeleeMode();
        m.Step(In());
        Assert.Equal(MeleeMode.Change.Entered, m.Step(In(strike: true)));
        for (int i = 0; i < 10; i++) m.Step(In(strike: true));
        m.Step(In());
        Assert.True(m.Active);
        return m;
    }

    // ---- enter ----

    [Theory]
    [InlineData("mp_melee_chainsword", true)]
    [InlineData("mp_melee_power_axe", true)]
    [InlineData("mp_melee_powersword", true)]
    [InlineData("mp_melee_2hand_thunder_hammer", true)]
    [InlineData("mp_melee_2hand_power_mace", true)]
    [InlineData("mp_melee_combat_knife", true)]   // owner 2026-11-25: the knife frees the camera too
    [InlineData("csm_mp_melee_combat_knife", true)]
    [InlineData("sm_mp_core_abil_heavy_evade", false)]   // the Devastator's kick / stomp: no melee weapon
    [InlineData("ork_coop_boy_choppa", true)]
    [InlineData(null, false)]
    public void KnifeAndMeleeWeaponsEnterNotTheKick(string? wargear, bool enters) => Assert.Equal(enters, MeleeMode.EntersMeleeMode(wargear));

    [Fact]
    public void TheKnifeAndTheMeleeWeaponsStrikesAndTheLungeFreeTheCamera()
    {
        Assert.True(MeleeMode.FreesCamera(1, "mp_melee_combat_knife", RushPhase.None));
        Assert.True(MeleeMode.FreesCamera(2, "mp_melee_chainsword", RushPhase.None));
        foreach (var dash in new[] { RushPhase.DashAttack, RushPhase.DashHit, RushPhase.DashStop, RushPhase.DashCollision })
            Assert.True(MeleeMode.FreesCamera(0, null, dash));   // the dash attack's lunge, whatever the class's melee
    }

    [Fact]
    public void TheChargeThePoundTheKickAndNoMoveDoNot()
    {
        foreach (var phase in Enum.GetValues<RushPhase>())
            if (!Rush.IsDash(phase)) Assert.False(MeleeMode.FreesCamera(0, "mp_melee_chainsword", phase));   // the bull rush (charge) in every phase
        Assert.False(MeleeMode.FreesCamera(0, "mp_melee_2hand_thunder_hammer", RushPhase.None));   // a ground pound is a jump phase, no strike: nothing here frees it
        Assert.False(MeleeMode.FreesCamera(1, "sm_mp_core_abil_heavy_evade", RushPhase.None));     // the Devastator's kick / stomp
        Assert.False(MeleeMode.FreesCamera(1, null, RushPhase.None));
    }

    [Fact]
    public void AStrikeEntersAndItLastsWithoutATimer()
    {
        var m = Entered();
        for (int i = 0; i < 30 * 60; i++) Assert.Equal(MeleeMode.Change.None, m.Step(In()));
        Assert.True(m.Active);
    }

    [Fact]
    public void NoStrikeNoMode()
    {
        var m = new MeleeMode();
        for (int i = 0; i < 100; i++) m.Step(In());
        Assert.False(m.Active);
    }

    // ---- exit ----

    [Fact] public void AShotLeaves() { var m = Entered(); Assert.Equal(MeleeMode.Change.Shot, m.Step(In(fire: true))); Assert.False(m.Active); }
    [Fact] public void AimingLeaves() { var m = Entered(); Assert.Equal(MeleeMode.Change.Aimed, m.Step(In(aim: true))); Assert.False(m.Active); }
    [Fact] public void AReloadPressLeaves() { var m = Entered(); Assert.Equal(MeleeMode.Change.Reloaded, m.Step(In(reload: true))); Assert.False(m.Active); }
    [Fact] public void DeathLeaves() { var m = Entered(); Assert.Equal(MeleeMode.Change.Died, m.Step(In(alive: false))); Assert.False(m.Active); }
    [Fact] public void ExclusionLeaves() { var m = Entered(); Assert.Equal(MeleeMode.Change.Excluded, m.Step(In(excluded: true))); Assert.False(m.Active); }

    [Fact]
    public void RespawnStartsOutsideTheMode()
    {
        var m = Entered();
        m.Step(In(alive: false));
        Assert.Equal(MeleeMode.Change.None, m.Step(In()));   // the respawn tick: already out
        Assert.False(m.Active);
    }

    [Fact]
    public void ReloadHeldIntoAStrikeDoesNotLeaveAgain()
    {
        var m = new MeleeMode();
        m.Step(In(reload: true));
        m.Step(In(reload: true, strike: true));
        m.Step(In(reload: true));
        Assert.True(m.Active);   // a press leaves, not a hold that began before the strike
    }

    [Fact]
    public void ThrowsAndSprintsKeepIt()
    {
        // Neither a grenade throw nor sprinting is an input the mode reads: only fire, aim, reload, death and respawn leave it.
        var m = Entered();
        for (int i = 0; i < 60; i++) Assert.Equal(MeleeMode.Change.None, m.Step(In()));
        Assert.True(m.Active);
    }

    [Fact]
    public void FireHeldBeforeTheStrikeEndsItOnceTheStrikeIsOver()
    {
        var m = new MeleeMode();
        m.Step(In(fire: true));
        Assert.Equal(MeleeMode.Change.Entered, m.Step(In(fire: true, strike: true)));   // a strike start beats the fire on the same tick
        m.Step(In(fire: true, strike: true));
        Assert.True(m.Active);
        Assert.Equal(MeleeMode.Change.Shot, m.Step(In(fire: true)));
    }

    [Fact]
    public void NoEntryWhileAiming()
    {
        var m = new MeleeMode();
        m.Step(In(aim: true, strike: true));
        Assert.False(m.Active);
    }

    [Fact]
    public void TheNextStrikeOfAStringEntersAgainAfterAFirePressThatEndedTheMode()
    {
        var m = Entered();
        m.Step(In(strike: true));
        Assert.Equal(MeleeMode.Change.Shot, m.Step(new MeleeMode.Inputs(true, true, true, false, false, false)));   // a fire press during the string (it fires nothing)
        Assert.False(m.Active);
        m.Step(In(strike: true));                                                                                  // the same strike runs on: no re-entry
        Assert.False(m.Active);
        Assert.Equal(MeleeMode.Change.Entered, m.Step(new MeleeMode.Inputs(true, true, false, false, false, false, NewStrike: true)));   // the next step of the string
    }

    [Fact]
    public void ANewStrikeAfterAShotEntersAgain()
    {
        var m = Entered();
        m.Step(In(fire: true));
        m.Step(In());
        Assert.Equal(MeleeMode.Change.Entered, m.Step(In(strike: true)));
    }

    // ---- a finisher (executions; owner 2026-11-26, "Melee cam": "Ignore them") ----

    static MeleeMode.Inputs Locked(bool fire = false, bool aim = false, bool reload = false) => In(fire: fire, aim: aim, reload: reload) with { Locked = true };

    [Fact]
    public void FireAimAndReloadPressedDuringAFinisherStayInMeleeMode()
    {
        var m = Entered();
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(MeleeMode.Change.None, m.Step(Locked()));
            Assert.Equal(MeleeMode.Change.None, m.Step(Locked(fire: true)));   // press, hold, release: nothing
            Assert.Equal(MeleeMode.Change.None, m.Step(Locked(fire: true)));
            Assert.Equal(MeleeMode.Change.None, m.Step(Locked()));
            Assert.Equal(MeleeMode.Change.None, m.Step(Locked(aim: true)));
            Assert.Equal(MeleeMode.Change.None, m.Step(Locked()));
            Assert.Equal(MeleeMode.Change.None, m.Step(Locked(reload: true)));
            Assert.Equal(MeleeMode.Change.None, m.Step(Locked()));
        }
        Assert.Equal(MeleeMode.Change.None, m.Step(In()));   // the End: still in melee mode
        Assert.True(m.Active);
    }

    [Theory]
    [InlineData(true, false, false, MeleeMode.Change.Shot)]
    [InlineData(false, true, false, MeleeMode.Change.Aimed)]
    [InlineData(false, false, true, MeleeMode.Change.Reloaded)]
    public void AFreshPressAfterTheFinisherLeaves(bool fire, bool aim, bool reload, MeleeMode.Change why)
    {
        var m = Entered();
        m.Step(Locked(fire, aim, reload));
        m.Step(Locked());
        m.Step(In());   // the End
        Assert.True(m.Active);
        Assert.Equal(why, m.Step(In(fire: fire, aim: aim, reload: reload)));
        Assert.False(m.Active);
    }

    [Theory]
    [InlineData(true, false, false, MeleeMode.Change.Shot)]
    [InlineData(false, true, false, MeleeMode.Change.Aimed)]
    [InlineData(false, false, true, MeleeMode.Change.Reloaded)]
    public void APressHeldAcrossTheEndStaysUntilReleasedAndPressedAgain(bool fire, bool aim, bool reload, MeleeMode.Change why)
    {
        var m = Entered();
        m.Step(Locked(fire, aim, reload));
        m.Step(Locked(fire, aim, reload));
        for (int i = 0; i < 20; i++) Assert.Equal(MeleeMode.Change.None, m.Step(In(fire: fire, aim: aim, reload: reload)));   // held past the End
        Assert.True(m.Active);
        Assert.Equal(MeleeMode.Change.None, m.Step(In()));   // released
        Assert.True(m.Active);
        Assert.Equal(why, m.Step(In(fire: fire, aim: aim, reload: reload)));   // pressed again
        Assert.False(m.Active);
    }

    [Fact]
    public void FireHeldThroughAStrikeIntoAFinisherIsSpentToo()
    {
        var m = new MeleeMode();
        m.Step(In(fire: true));
        Assert.Equal(MeleeMode.Change.Entered, m.Step(In(fire: true, strike: true)));
        m.Step(Locked(fire: true));                                  // the finisher starts with fire still held
        Assert.Equal(MeleeMode.Change.None, m.Step(In(fire: true)));  // the End, fire held on: no shot leaves
        Assert.True(m.Active);
    }

    [Fact]
    public void DeathDuringAFinisherStillLeaves()
    {
        var m = Entered();
        m.Step(Locked());
        Assert.Equal(MeleeMode.Change.Died, m.Step(Locked() with { Alive = false }));
        Assert.False(m.Active);
    }

    [Fact]
    public void WithoutAFinisherTheRulesAreUnchanged()
    {
        // Locked defaults to false: a hold that began in melee mode still leaves (fire held, aim held), as before the finisher rule.
        var m = Entered();
        Assert.Equal(MeleeMode.Change.Shot, m.Step(In(fire: true)));
        m = Entered();
        m.Step(In());
        Assert.Equal(MeleeMode.Change.Aimed, m.Step(In(aim: true)));
    }

    // ---- before the Start arrives: the Execute press holds the mode (lead 41) ----

    [Fact]
    public void TheWaitForTheStartIsAtLeast06SecondsOrTwiceTheRoundTrip()
    {
        Assert.Equal(0.6, FinisherPressHold.Bound(0.0), 9);
        Assert.Equal(0.6, FinisherPressHold.Bound(0.2), 9);
        Assert.Equal(1.0, FinisherPressHold.Bound(0.5), 9);
        Assert.Equal(0.6, FinisherPressHold.Bound(-1.0), 9);
    }

    [Fact]
    public void APressThenALateStartStaysInMeleeModeThroughTheWholeFinisher()
    {
        var m = Entered();
        var hold = new FinisherPressHold();
        double bound = FinisherPressHold.Bound(0.1);
        hold.Press(10.0);
        double t = 10.0;
        // The round trip: no Start yet; a fire press, a release and an aim press do nothing.
        foreach (var (fire, aim) in new[] { (false, false), (true, false), (false, false), (false, true), (false, false) })
        {
            t += 0.1;
            bool held = hold.Held(t, live: false, endedSincePress: false, bound);
            Assert.True(held);
            Assert.Equal(MeleeMode.Change.None, m.Step(In(fire: fire, aim: aim) with { Locked = held }));
        }
        // The Start at 0.55 s: the real lock holds on (the speculative part lets go, the hook ORs the live run in).
        t = 10.55;
        Assert.False(hold.Held(t, live: true, endedSincePress: false, bound));
        for (int i = 0; i < 30; i++) Assert.Equal(MeleeMode.Change.None, m.Step(In(reload: i % 2 == 0) with { Locked = true }));
        // The End: the press is forgotten, still in melee mode; a fresh press leaves.
        Assert.False(hold.Held(t + 2.0, live: false, endedSincePress: false, bound));
        Assert.Equal(MeleeMode.Change.None, m.Step(In()));
        Assert.True(m.Active);
        Assert.Equal(MeleeMode.Change.Shot, m.Step(In(fire: true)));
    }

    [Fact]
    public void APressWithNoStartReleasesTheHoldAfterTheBound()
    {
        var m = Entered();
        var hold = new FinisherPressHold();
        double bound = FinisherPressHold.Bound(0.05);   // 0.6 s
        hold.Press(5.0);
        Assert.True(hold.Held(5.3, false, false, bound));
        Assert.True(hold.Held(5.6, false, false, bound));
        Assert.Equal(MeleeMode.Change.None, m.Step(In(fire: true) with { Locked = true }));   // a press in the wait: nothing
        Assert.False(hold.Held(5.61, false, false, bound));                                  // refused or lost: the normal rule again
        Assert.False(hold.Held(5.7, false, false, bound));
        Assert.Equal(MeleeMode.Change.None, m.Step(In(fire: true)));   // the press made in the wait is spent until released
        m.Step(In());
        Assert.Equal(MeleeMode.Change.Shot, m.Step(In(fire: true)));   // a fresh press leaves
    }

    [Fact]
    public void AnEndHeardAfterThePressOrNoPressHoldsNothing()
    {
        var hold = new FinisherPressHold();
        Assert.False(hold.Held(1.0, false, false, 0.6));   // no press
        Assert.True(double.IsNaN(hold.PressedAt));
        hold.Press(2.0);
        Assert.Equal(2.0, hold.PressedAt);
        Assert.False(hold.Held(2.2, live: false, endedSincePress: true, 0.6));   // the run started and ended before a live list showed it
        Assert.False(hold.Held(2.3, false, false, 0.6));                         // forgotten
        hold.Press(3.0);
        hold.Reset();
        Assert.False(hold.Held(3.1, false, false, 0.6));                         // a new connection
    }

    // ---- the body yaw ----

    [Fact]
    public void TheStickIsReadRelativeToTheCamera()
    {
        float cam = 0.7f;
        Assert.Equal(cam, MeleeOrbitSteer.StickYaw(cam, 0f, 1f), 5);                       // W: the camera's look
        Assert.Equal(cam - MathF.PI / 2f, MeleeOrbitSteer.StickYaw(cam, 1f, 0f), 5);        // D: to the camera's right (yaw grows left)
        Assert.Equal(cam + MathF.PI / 2f, MeleeOrbitSteer.StickYaw(cam, -1f, 0f), 5);       // A
        Assert.Equal(MathF.PI, MathF.Abs(MeleeOrbitSteer.Wrap(MeleeOrbitSteer.StickYaw(cam, 0f, -1f) - cam)), 4);   // S: toward the camera
    }

    [Fact]
    public void TheStickIsSentAsForwardWithItsMagnitude()
    {
        var r = new MeleeOrbitSteer().Step(0f, 0f, 0.6f, 0.8f, snap: true, strikePress: false, Tick);
        Assert.Equal(0f, r.MoveX);
        Assert.Equal(1f, r.MoveY, 4);
        var half = new MeleeOrbitSteer().Step(0f, 0f, 0f, 0.5f, snap: false, strikePress: false, Tick);
        Assert.Equal(0.5f, half.MoveY, 4);
    }

    [Fact]
    public void IdleStickHoldsTheBody()
    {
        var steer = new MeleeOrbitSteer();
        for (int i = 0; i < 30; i++)
        {
            var r = steer.Step(cameraYaw: i * 0.2f, bodyYaw: 1.25f, 0f, 0f, snap: false, strikePress: false, Tick);   // the camera orbits, nothing held
            Assert.Equal(1.25f, r.Yaw);
            Assert.Equal((0f, 0f), (r.MoveX, r.MoveY));
        }
    }

    [Fact]
    public void AStrikeWithNoKeyGoesTheWayTheCameraLooks()
    {
        var r = new MeleeOrbitSteer().Step(cameraYaw: 2.5f, bodyYaw: -0.4f, 0f, 0f, snap: true, strikePress: true, Tick);
        Assert.Equal(2.5f, r.Yaw, 5);
    }

    [Fact]
    public void AStrikeWithAKeyGoesAlongTheKeyAtOnce()
    {
        var r = new MeleeOrbitSteer().Step(cameraYaw: 0f, bodyYaw: 0f, 1f, 0f, snap: true, strikePress: true, Tick);
        Assert.Equal(-MathF.PI / 2f, r.Yaw, 5);
        Assert.Equal(1f, r.MoveY, 5);
    }

    [Fact]
    public void TheBodyTurnsAtTheDataRateAfterTheRamp()
    {
        var steer = new MeleeOrbitSteer();
        float body = 0f;
        // The first tick: the ramp's share (1/30 s of 0.15 s) of 1080 degrees a second for one tick.
        var first = steer.Step(MathF.PI - 0.01f, body, 0f, 1f, false, false, Tick);
        Assert.Equal(1080f * (Tick / 0.15f) * Tick, first.Yaw / Deg, 2);
        body = first.Yaw;
        int ticks = 1;
        while (MathF.Abs(MeleeOrbitSteer.Wrap(MathF.PI - 0.01f - body)) > 1e-4f && ticks < 100) { body = steer.Step(MathF.PI - 0.01f, body, 0f, 1f, false, false, Tick).Yaw; ticks++; }
        // About 180 degrees: 81 degrees in the 0.15 s ramp, the rest at 1080 a second: 0.24 s, 8 ticks.
        Assert.InRange(ticks, 7, 9);
        // At full rate a tick turns 36 degrees.
        var full = new MeleeOrbitSteer();
        for (int i = 0; i < 5; i++) full.Step(1f, 1f, 0f, 1f, false, false, Tick);   // arrived each tick: the ramp restarts
        float b = 0f, prev = 0f;
        for (int i = 0; i < 6; i++) { prev = b; b = full.Step(3f, b, 0f, 1f, false, false, Tick).Yaw; }
        Assert.Equal(36f, (b - prev) / Deg, 1);
    }

    // The simulation side (server and prediction stepping the same input, and the review cases): MeleeOrbitClientTests.
}
