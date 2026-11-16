namespace SmNet.Tests;

/// <summary>The test bots' melee reaction time (night batch 2026-11-06; a test aid, ours, an estimate).</summary>
public class BotMeleeReactionTests
{
    const float Dt = 1f / 30f;

    static int TicksUntilReady(BotMeleeReaction r, int target)
    {
        int n = 0;
        while (!r.Step(Dt, target)) n++;
        return n;
    }

    [Fact]
    public void The_first_melee_action_is_never_instant_and_waits_200_to_350_ms()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            int ticks = TicksUntilReady(new BotMeleeReaction(seed), 3);
            Assert.True(ticks >= 5, $"seed {seed}: {ticks} ticks");
            Assert.InRange(ticks * Dt, BotMeleeReaction.ReactionMin - Dt, BotMeleeReaction.ReactionMax + Dt);
        }
    }

    [Fact]
    public void A_new_target_or_a_target_back_in_reach_waits_again_but_a_steady_one_does_not()
    {
        var r = new BotMeleeReaction(5);
        TicksUntilReady(r, 3);
        Assert.True(r.Step(Dt, 3));                                    // same target, still in reach: ready
        Assert.False(r.Step(Dt, 4));                                   // another target: waits
        TicksUntilReady(r, 4);
        for (int i = 0; i < 15; i++) Assert.False(r.Step(Dt, null));   // out of reach for 0.5 s
        Assert.False(r.Step(Dt, 4));                                   // back in reach: new acquisition
    }

    [Fact]
    public void A_new_threat_re_arms_the_wait()
    {
        var r = new BotMeleeReaction(8);
        TicksUntilReady(r, 3);
        Assert.False(r.Step(Dt, 3, threatBegan: true));                // an enemy starts an attack: the answer is not on the same tick
        Assert.True(TicksUntilReady(r, 3) >= 5);
    }

    [Fact]
    public void The_same_seed_gives_the_same_waits()
    {
        Assert.Equal(TicksUntilReady(new BotMeleeReaction(3), 1), TicksUntilReady(new BotMeleeReaction(3), 1));
    }
}
