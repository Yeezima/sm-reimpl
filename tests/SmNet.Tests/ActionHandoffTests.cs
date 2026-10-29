using SmNet;

namespace SmNet.Tests;

/// <summary>M40-T4 follow-up: the rules of the action-clip hand-off (<see cref="ActionHandoff"/>).</summary>
public class ActionHandoffTests
{
    [Theory]
    [InlineData(true, true, true, 3f, true)]
    [InlineData(true, true, true, 0.4f, false)]     // not above the threshold
    [InlineData(true, true, true, 0.5f, false)]     // strictly above
    [InlineData(false, true, true, 3f, false)]      // dead
    [InlineData(true, false, true, 3f, false)]      // in the air
    [InlineData(true, true, false, 3f, false)]      // in a jump-pack phase
    public void The_body_moves_only_alive_on_the_ground_without_a_jump_phase_above_the_threshold(bool alive, bool grounded, bool jumpNone, float speed, bool expected) =>
        Assert.Equal(expected, ActionHandoff.Moving(alive, grounded, jumpNone, speed));

    [Fact]
    public void An_attack_hands_over_only_after_its_movement_lock_and_while_the_body_moves()
    {
        Assert.False(ActionHandoff.AttackHandsOver(0.3f, 0.5f, true));     // still locked
        Assert.True(ActionHandoff.AttackHandsOver(0.5f, 0.5f, true));
        Assert.False(ActionHandoff.AttackHandsOver(0.8f, 0.5f, false));    // standing
    }

    [Fact]
    public void A_new_step_or_heavy_switch_or_backward_time_is_another_attack()
    {
        Assert.False(ActionHandoff.AttackChanged(1, false, 0.4f, 1, false, 0.45f));
        Assert.True(ActionHandoff.AttackChanged(1, false, 0.4f, 2, false, 0.0f));
        Assert.True(ActionHandoff.AttackChanged(1, false, 0.4f, 1, true, 0.45f));
        Assert.True(ActionHandoff.AttackChanged(1, false, 0.4f, 1, false, 0.0f));   // the same step restarted
        Assert.False(ActionHandoff.AttackChanged(1, false, 0.4f, 1, false, 0.4f));  // an unchanged time is not a restart
    }

    [Fact]
    public void The_tail_fades_the_clip_out_over_its_last_moments()
    {
        Assert.Equal(1f, ActionHandoff.TailWeight(1f, 0.5f));
        Assert.Equal(1f, ActionHandoff.TailWeight(1f, 1f - ActionHandoff.TailSeconds), 3);
        Assert.Equal(0.5f, ActionHandoff.TailWeight(1f, 1f - 0.5f * ActionHandoff.TailSeconds), 3);
        Assert.Equal(0f, ActionHandoff.TailWeight(1f, 1f));
        Assert.Equal(0f, ActionHandoff.TailWeight(1f, 1.2f));
    }

    [Fact]
    public void A_throw_fades_in_over_its_first_moments_and_out_over_its_last()
    {
        Assert.Equal(0f, ActionHandoff.ThrowWeight(0.667f, 0f));
        Assert.Equal(0.5f, ActionHandoff.ThrowWeight(0.667f, 0.5f * ActionHandoff.HeadSeconds), 3);
        Assert.Equal(1f, ActionHandoff.ThrowWeight(0.667f, 0.3f));
        Assert.Equal(0f, ActionHandoff.ThrowWeight(0.667f, 0.667f));
        Assert.True(ActionHandoff.ThrowRestarted(0.5f, 0.02f));
        Assert.False(ActionHandoff.ThrowRestarted(0.02f, 0.5f));
    }
}
