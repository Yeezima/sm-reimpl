using SmContent;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Install transitions, pass 2 batch 2 (review repairs): the dash's clip family against the simulation's ticks, as the own player and as a remote or spectated player see it. A ground
/// tap's press tick starts the jump-pack take-off and already lowers the weapon (<c>jump.Started</c>, ReadyTicks 0); the dash begins on the release tick. The stance the client reads on
/// each non-evading frame (the hammer stance unless the pistol is up, as <c>NetworkView.HammerStance</c> / <c>PistolUp</c>) is fed to <see cref="DashFamilyLatch"/>, take-off frames
/// flagged as the client flags them: the own (full) record by its <see cref="JumpPhase.TakeOff"/>, a remote's slim record (<see cref="Protocol.Slim"/>, which has no take-off phase and
/// whose weapon-up bit is 0 on the press tick) by <see cref="DashFamilyLatch.SlimTakingOff"/>.
/// </summary>
public class HammerDashStanceTests
{
    const string Hammer = "sm_mp_melee_2hand_thunder_hammer";
    const float TakeOffDelay = 0.2f;

    /// <summary>The client's pistol-up rule (<c>NetworkView.PistolUp</c>): aim mode, the weapon held ready after a shot, raising, charging or with a shot pending.</summary>
    static bool PistolUp(PlayerState s) => s.Aiming || s.ReadyTicks > 0 || s.RaiseTicks > 0 || s.Charging || s.PendingShot > 0;

    static string? Stance(PlayerState s) => PistolUp(s) ? null : Hammer;

    /// <summary>The simulation's ticks of a ground tap from <paramref name="start"/>: the frame before the press, the press tick, <paramref name="holdTicks"/> more ticks held, then the release tick (the dash).</summary>
    static List<PlayerState> Ticks(PlayerState start, int holdTicks = 0)
    {
        var c = JumpPackDashTests.Config;
        var s = start;
        var list = new List<PlayerState> { s };
        for (int i = 0; i <= holdTicks; i++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Jump), c, out _);
            Assert.Equal(JumpPhase.TakeOff, s.Jump);
            Assert.False(Evade.Evading(s));
            list.Add(s);
        }
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.None), c, out _);
        Assert.True(Evade.Evading(s));   // released: the dash
        list.Add(s);
        return list;
    }

    enum View { Own, Remote, RemoteOldRule }

    /// <summary>The family latched on the first evading frame of <paramref name="states"/> as <paramref name="view"/> reads them; <paramref name="skip"/>: frames the viewer never sees.</summary>
    static DashFamily Family(List<PlayerState> states, View view, params int[] skip)
    {
        var dt = JumpPackDashTests.Config.TickSeconds;
        var latch = new DashFamilyLatch();
        for (int i = 0; i < states.Count; i++)
        {
            if (skip.Contains(i)) continue;
            var s = view == View.Own ? states[i] : Protocol.Slim(states[i]);
            if (Evade.Evading(s)) return latch.During(s.PlayerId, Stance(s), false, out _);
            bool takingOff = view switch
            {
                View.Own => s.Jump == JumpPhase.TakeOff,
                View.RemoteOldRule => s.Jump == JumpPhase.TakeOff,   // 58ca8626's rule on a slim record
                _ => latch.SlimTakingOff(s.PlayerId, JumpPack.Jumping(s.Jump), s.Grounded, i * dt, TakeOffDelay),
            };
            latch.Ground(s.PlayerId, Stance(s), takingOff);
        }
        throw new InvalidOperationException("no evading frame");
    }

    static PlayerState Fresh() => Combat.Respawned(new PlayerState(1, default, default, 0f, true) { Class = 0 }, JumpPackDashTests.Config);
    static PlayerState PistolUpStart() => Fresh() with { ReadyTicks = JumpPackDashTests.Config.ReadyHoldTicks };
    static PlayerState HammerStart() => Fresh() with { ReadyTicks = 0 };

    [Fact]
    public void A_ground_tap_with_the_pistol_up_gives_the_plain_dash()
    {
        var states = Ticks(PistolUpStart());
        Assert.True(PistolUp(states[0]));
        Assert.False(PistolUp(states[1]));   // the press tick has lowered the weapon: that frame alone would read the hammer stance
        Assert.Equal(DashFamily.Plain, Family(states, View.Own));
    }

    [Fact]
    public void A_ground_tap_in_the_hammer_stance_gives_the_hammers_dash()
    {
        var states = Ticks(HammerStart());
        Assert.False(PistolUp(states[0]));
        Assert.Equal(DashFamily.Hammer, Family(states, View.Own));
    }

    [Fact]
    public void The_slim_record_has_no_take_off_phase_and_no_weapon_up_on_the_press_tick()
    {
        var press = Protocol.Slim(Ticks(PistolUpStart())[1]);
        Assert.Equal(JumpPhase.Ascent, press.Jump);
        Assert.True(press.Grounded);
        Assert.False(PistolUp(press));
    }

    [Fact]
    public void A_remote_sees_the_plain_dash_for_a_pistol_up_tap_and_the_old_rule_did_not()
    {
        var states = Ticks(PistolUpStart());
        Assert.Equal(DashFamily.Plain, Family(states, View.Remote));
        Assert.Equal(DashFamily.Hammer, Family(states, View.RemoteOldRule));   // 58ca8626: the slim press tick overwrote the stance
    }

    [Fact]
    public void A_remote_sees_the_hammers_dash_for_a_tap_in_the_hammer_stance()
    {
        var states = Ticks(HammerStart());
        Assert.Equal(DashFamily.Hammer, Family(states, View.Remote));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Own_and_remote_agree_on_a_held_tap_and_with_the_take_off_snapshot_dropped(int holdTicks)
    {
        foreach (var start in new[] { PistolUpStart(), HammerStart() })
        {
            var states = Ticks(start, holdTicks);
            var own = Family(states, View.Own);
            Assert.Equal(PistolUp(start) ? DashFamily.Plain : DashFamily.Hammer, own);
            Assert.Equal(own, Family(states, View.Remote));
            // Every take-off snapshot dropped (loss, a slow frame): the stance is the frame-before-the-press one.
            Assert.Equal(own, Family(states, View.Remote, Enumerable.Range(1, holdTicks + 1).ToArray()));
            // Only the first take-off snapshot dropped (a held tap): the later take-off frames are still skipped.
            if (holdTicks > 0) Assert.Equal(own, Family(states, View.Remote, 1));
        }
    }

    [Fact]
    public void A_take_off_that_left_the_ground_counts_as_the_take_off_until_its_delay_is_over()
    {
        var latch = new DashFamilyLatch();
        Assert.False(latch.SlimTakingOff(1, jumping: false, grounded: true, 0.0, TakeOffDelay));
        Assert.True(latch.SlimTakingOff(1, jumping: true, grounded: true, 0.1, TakeOffDelay));    // the press tick
        Assert.True(latch.SlimTakingOff(1, jumping: true, grounded: false, 0.2, TakeOffDelay));   // walked off the edge before the release (own record: still the take-off)
        Assert.False(latch.SlimTakingOff(1, jumping: true, grounded: false, 0.31, TakeOffDelay)); // past the delay: the flight, read as the own view reads it
        Assert.False(latch.SlimTakingOff(1, jumping: false, grounded: false, 0.5, TakeOffDelay)); // the jump over: the window forgotten
        Assert.False(latch.SlimTakingOff(1, jumping: true, grounded: false, 0.6, TakeOffDelay));  // an air jump with no grounded take-off
    }

    [Fact]
    public void A_chained_dash_keeps_the_first_dashs_stance()
    {
        var latch = new DashFamilyLatch();
        latch.Ground(1, null);   // pistol up before the first press
        latch.Ground(1, Hammer, takingOff: true);
        Assert.Equal(DashFamily.Plain, latch.During(1, Hammer, false, out _));
        latch.Ground(1, Hammer, takingOff: true);   // the quick re-press at the landing: a take-off frame
        Assert.False(latch.IsLatched(1));
        Assert.Equal(DashFamily.Plain, latch.During(1, Hammer, false, out _));
    }
}
