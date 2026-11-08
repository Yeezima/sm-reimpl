using SmSim;

namespace SmNet.Tests;

/// <summary>Owner, 2026-11-08: the latest bot pass runs in Seize Ground only; Annihilation plays as before it (<see cref="BotRecentAiRules"/>).</summary>
public class BotRecentAiModeTests
{
    [Fact]
    public void Annihilation_rolls_back_the_pass_and_Seize_keeps_it()
    {
        Assert.False(BotRecentAiRules.Applies(seizeGround: false, forced: false));
        Assert.True(BotRecentAiRules.Applies(seizeGround: true, forced: false));
    }

    [Fact]
    public void The_test_flag_turns_the_pass_on_in_Annihilation()
    {
        Assert.True(BotRecentAiRules.Applies(seizeGround: false, forced: true));
        Assert.True(BotRecentAiRules.Applies(seizeGround: true, forced: true));
    }

    [Fact]
    public void The_old_push_step_has_no_under_fire_rule()
    {
        // The Annihilation path calls Step without sinceDamage/closedIn: a push may start right after a hit and is not ended by one.
        var push = new BotPush(1);
        bool started = false;
        for (double t = 0; t < 200 && !started; t += 0.5) started = push.Step(t, "assault", 7, 20f, true, false);
        Assert.True(started);
    }
}
