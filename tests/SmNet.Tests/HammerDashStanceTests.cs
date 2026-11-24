using SmContent;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Install transitions, pass 2 batch 2 (review repair): the dash's clip family against the simulation's ticks. A ground tap's press tick starts the jump-pack take-off and already
/// lowers the weapon (<c>jump.Started</c>, ReadyTicks 0); the dash begins a tick later. The stance the client reads on each non-evading frame (the hammer stance unless the pistol is
/// up, as <c>NetworkView.HammerStance</c> / <c>PistolUp</c>) is fed to <see cref="DashFamilyLatch"/>, take-off frames flagged as the client flags them.
/// </summary>
public class HammerDashStanceTests
{
    const string Hammer = "sm_mp_melee_2hand_thunder_hammer";

    /// <summary>The client's pistol-up rule (<c>NetworkView.PistolUp</c>): aim mode, the weapon held ready after a shot, raising, charging or with a shot pending.</summary>
    static bool PistolUp(PlayerState s) => s.Aiming || s.ReadyTicks > 0 || s.RaiseTicks > 0 || s.Charging || s.PendingShot > 0;

    /// <summary>A ground tap (press, then release) from <paramref name="start"/>; returns the family latched on the first evading frame, and whether the stance read on the press frame was the pistol's.</summary>
    static (DashFamily Family, bool PistolUpOnTakeOff) Tap(PlayerState start, bool honourTakeOff = true)
    {
        var c = JumpPackDashTests.Config;
        var latch = new DashFamilyLatch();
        var s = start;
        void Frame()
        {
            if (!Evade.Evading(s)) latch.Ground(s.PlayerId, PistolUp(s) ? null : Hammer, takingOff: honourTakeOff && s.Jump == JumpPhase.TakeOff);
        }
        Frame();   // the frame before the press
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Jump), c, out _);
        Assert.Equal(JumpPhase.TakeOff, s.Jump);
        Assert.False(Evade.Evading(s));
        bool pistolOnTakeOff = PistolUp(s);
        Frame();   // the press tick: taking off, weapon already lowered
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.None), c, out _);
        Assert.True(Evade.Evading(s));   // released: the dash
        return (latch.During(s.PlayerId, PistolUp(s) ? null : Hammer, false, out _), pistolOnTakeOff);
    }

    static PlayerState Fresh() => Combat.Respawned(new PlayerState(1, default, default, 0f, true) { Class = 0 }, JumpPackDashTests.Config);

    [Fact]
    public void A_ground_tap_with_the_pistol_up_gives_the_plain_dash()
    {
        var c = JumpPackDashTests.Config;
        var start = Fresh() with { ReadyTicks = c.ReadyHoldTicks };
        Assert.True(PistolUp(start));
        var (family, pistolOnTakeOff) = Tap(start);
        Assert.False(pistolOnTakeOff);   // the press tick has lowered the weapon: that frame alone would read the hammer stance
        Assert.Equal(DashFamily.Plain, family);
        Assert.Equal(DashFamily.Hammer, Tap(start, honourTakeOff: false).Family);   // what the frame-before-the-dash rule gave without the take-off exception
    }

    [Fact]
    public void A_ground_tap_in_the_hammer_stance_gives_the_hammers_dash()
    {
        var start = Fresh() with { ReadyTicks = 0 };
        Assert.False(PistolUp(start));
        Assert.Equal(DashFamily.Hammer, Tap(start).Family);
    }
}
