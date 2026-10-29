using SmClient.ImpactRules;
using SmSim;

namespace SmNet.Tests;

/// <summary>fx/melee-presentation: other players' melee and charge hits are confirmed by their victim's reaction before their sound plays.</summary>
public class MeleeHitConfirmTests
{
    static List<MeleeHitConfirm.Hit> Drain(MeleeHitConfirm c, double now) { var l = new List<MeleeHitConfirm.Hit>(); c.Drain(now, l); return l; }

    [Fact]
    public void A_candidate_is_confirmed_when_the_victims_health_falls_in_the_window_and_once()
    {
        var c = new MeleeHitConfirm();
        c.Observe(2, true, 100f, 1.0);
        c.Add(1, 2, charge: false, dash: false, 1.1);
        Assert.Empty(Drain(c, 1.1));
        c.Observe(2, true, 60f, 1.2);
        var hit = Assert.Single(Drain(c, 1.2));
        Assert.Equal((1, 2, false), (hit.Attacker, hit.Victim, hit.Charge));
        Assert.Empty(Drain(c, 1.3));
        Assert.Equal(0, c.Pending);
    }

    [Fact]
    public void A_drop_a_little_before_the_candidate_confirms_it_a_missed_swing_expires_and_a_regen_is_no_reaction()
    {
        var c = new MeleeHitConfirm();
        c.Observe(2, true, 100f, 1.0);
        c.Observe(2, true, 70f, 1.05);          // the snapshot with the damage came first
        c.Add(1, 2, false, false, 1.1);
        Assert.Single(Drain(c, 1.1));
        // A swing at someone who does not react.
        c.Observe(3, true, 100f, 2.0);
        c.Add(1, 3, false, false, 2.0);
        c.Observe(3, true, 100f, 2.2);
        Assert.Empty(Drain(c, 2.2));
        Assert.Empty(Drain(c, 2.0 + MeleeHitConfirm.After + 0.01));
        Assert.Equal(0, c.Pending);
        // Shield regenerating upward, or a tiny change, is not a reaction.
        c.Observe(4, true, 50f, 3.0); c.Observe(4, true, 80f, 3.1); c.Observe(4, true, 79.8f, 3.2);
        c.Add(1, 4, false, false, 3.2);
        Assert.Empty(Drain(c, 3.3));
    }

    [Fact]
    public void A_victim_killed_by_the_blow_confirms_it_and_an_old_drop_does_not()
    {
        var c = new MeleeHitConfirm();
        c.Observe(2, true, 10f, 5.0);
        c.Observe(2, false, 0f, 5.1);
        c.Add(1, 2, true, true, 5.1);
        var hit = Assert.Single(Drain(c, 5.1));
        Assert.True(hit.Charge && hit.Dash);
        // A drop long before the candidate belongs to something else.
        c.Observe(3, true, 100f, 6.0); c.Observe(3, true, 50f, 6.1);
        c.Add(1, 3, false, false, 7.0);
        Assert.Empty(Drain(c, 7.0));
    }

    [Fact]
    public void Players_who_left_are_forgotten_and_a_charge_connects_only_into_its_stop_phase()
    {
        var c = new MeleeHitConfirm();
        c.Observe(2, true, 100f, 1.0);
        c.Add(1, 2, false, false, 1.0);
        c.Prune(_ => false);
        Assert.Equal(0, c.Pending);
        Assert.True(ChargeConnect.Connected(RushPhase.Rush, RushPhase.Collision));
        Assert.True(ChargeConnect.Connected(RushPhase.DashAttack, RushPhase.DashHit));
        Assert.False(ChargeConnect.Connected(RushPhase.DashAttack, RushPhase.DashCollision));   // a wall
        Assert.False(ChargeConnect.Connected(RushPhase.None, RushPhase.Collision));
        Assert.False(ChargeConnect.Connected(RushPhase.Collision, RushPhase.Collision));
    }
}
