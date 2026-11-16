using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// musou/no-aerial-tackle (owner, 2026-11-12, temporary): in Musou Mode the Assault's and Raptor's aerial tackle (the dash attack, a melee press in a hop's window) never starts, and a
/// melee press at any tick of a hop is the light attack at once with no wait after it; Classic PvP's tackle is as before; <c>--musou-aerial-tackle</c> (AerialTackleRestored) restores it.
/// </summary>
public class MusouNoAerialTackleTests(ITestOutputHelper output)
{
    static readonly SimConfig Classic = JumpPackAttackTests.Config;
    static readonly SimConfig Musou = Classic with { Extras = ContentExtras.Musou };
    static readonly SimConfig Restored = Musou with { AerialTackleRestored = true };

    static PlayerState Hop(SimConfig c, int ticks)
    {
        var s = MeleeCancelTests.Fresh(c);
        s = MeleeCancelTests.Step(s, c, InputButtons.Jump);
        s = MeleeCancelTests.Step(s, c, InputButtons.None);   // released before the decision tick: the hop
        Assert.True(Evade.Evading(s));
        for (int k = 0; k < ticks; k++) s = MeleeCancelTests.Step(s, c, InputButtons.None);
        return s;
    }

    [Fact]
    public void The_hook_is_on_for_musou_only_and_the_restore_switch_turns_it_off()
    {
        Assert.True(Musou.AerialTackleOff);
        Assert.False(Classic.AerialTackleOff);
        Assert.False(Restored.AerialTackleOff);
        Assert.False((Classic with { Extras = ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks }).AerialTackleOff);
        Assert.NotEqual(Musou.Hash(), Restored.Hash());   // clients and server must agree on the switch
    }

    [Fact]
    public void In_musou_a_melee_press_at_every_tick_of_a_hop_starts_a_light_attack_that_tick_and_never_the_tackle()
    {
        int tested = 0, windowTicks = 0;
        for (int ticks = 1; ticks < 120; ticks++)
        {
            var s = Hop(Musou, ticks);
            if (!Evade.Evading(s)) break;   // the hop is over
            if (Rush.CanDashAttack(s, Classic, null)) windowTicks++;   // the same state in Classic is inside the tackle's window
            Assert.False(Rush.CanDashAttack(s, Musou, null), $"tick {ticks}: no tackle window in Musou");
            var after = MeleeCancelTests.Step(s, Musou, InputButtons.Melee);
            Assert.Equal(RushPhase.None, after.Rush);
            Assert.True(after.MeleeStep > 0);                // a light attack starts in the press tick (Musou picks any step of the chain)
            Assert.False(Evade.Evading(after));
            Assert.Equal(0f, after.MeleeLock);              // no wait before the next swing
            tested++;
        }
        output.WriteLine($"{tested} hop ticks tested, {windowTicks} of them inside the Classic tackle window");
        Assert.True(tested >= 20);
        Assert.True(windowTicks > 0, "the test covers the ticks where Classic starts the tackle");
    }

    [Fact]
    public void In_musou_the_next_swing_is_not_held_after_a_hop_cancel()
    {
        var s = MeleeCancelTests.Step(Hop(Musou, 12), Musou, InputButtons.Melee);
        Assert.True(s.MeleeStep > 0);
        int guard = 0;
        while (s.MeleeStep > 0 && guard++ < 300) s = MeleeCancelTests.Step(s, Musou, InputButtons.None);
        Assert.Equal(0f, s.MeleeLock);
        s = MeleeCancelTests.Step(MeleeCancelTests.Step(s, Musou, InputButtons.None), Musou, InputButtons.Melee);
        Assert.True(s.MeleeStep > 0);
    }

    [Fact]
    public void In_classic_the_tackle_still_starts_in_its_window_and_the_early_cancel_keeps_its_wait()
    {
        var s = Hop(Classic, 12);
        Assert.True(Rush.CanDashAttack(s, Classic, null));
        Assert.Equal(RushPhase.DashAttack, MeleeCancelTests.Step(s, Classic, InputButtons.Melee).Rush);
        var early = MeleeCancelTests.Step(Hop(Classic, 3), Classic, InputButtons.Melee);
        Assert.Equal(1, early.MeleeStep);
        Assert.True(early.MeleeLock > 0f);
    }

    [Fact]
    public void The_restore_switch_brings_the_tackle_back_in_musou()
    {
        var s = Hop(Restored, 12);
        Assert.True(Rush.CanDashAttack(s, Restored, null));
        Assert.Equal(RushPhase.DashAttack, MeleeCancelTests.Step(s, Restored, InputButtons.Melee).Rush);
    }
}
