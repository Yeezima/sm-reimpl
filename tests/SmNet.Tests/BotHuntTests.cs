using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>The owner's hunt of 2026-11-16 (<see cref="BotHunt"/>; test aid, ours): whom a bot with nobody to chase hunts.</summary>
public class BotHuntTests
{
    static HuntCandidate E(int id, float x, bool room = false, bool zone = false) => new(id, new Vector3(x, 0, 0), room, zone);

    [Fact]
    public void An_enemy_in_its_spawn_room_is_never_hunted()
    {
        var h = new BotHunt();
        Assert.Null(h.Pick(Vector3.Zero, [E(1, 10, room: true), E(2, 30, room: true, zone: true)]));
        Assert.Null(h.Pick(Vector3.Zero, []));
        Assert.Equal(0, h.Hunts);
        Assert.Equal(2, h.Pick(Vector3.Zero, [E(1, 5, room: true), E(2, 80)]));
    }

    [Fact]
    public void An_enemy_in_the_no_loiter_zone_is_not_hunted_unless_the_zone_switch_is_on()
    {
        var h = new BotHunt();
        Assert.Equal(3, h.Pick(Vector3.Zero, [E(1, 5, zone: true), E(2, 7, room: true), E(3, 90)]));
        Assert.Null(h.Pick(Vector3.Zero, [E(1, 40, zone: true), E(2, 20, room: true)]));
        var h2 = new BotHunt();
        Assert.Equal(1, h2.Pick(Vector3.Zero, [E(1, 40, zone: true), E(2, 20, room: true), E(4, 60, zone: true)], allowZone: true));
        Assert.True(h2.InZone);
        Assert.Equal(3, h2.Pick(Vector3.Zero, [E(1, 5, zone: true), E(3, 90)], allowZone: true));   // outside the zone first
        Assert.False(h2.InZone);
    }

    [Fact]
    public void The_nearest_is_hunted_and_kept_until_another_is_15_m_nearer()
    {
        var h = new BotHunt();
        Assert.Equal(1, h.Pick(Vector3.Zero, [E(1, 40), E(2, 60)]));
        Assert.Equal(1, h.Pick(Vector3.Zero, [E(1, 40), E(2, 30)]));     // only 10 m nearer: keep
        Assert.Equal(2, h.Pick(Vector3.Zero, [E(1, 40), E(2, 20)]));     // 20 m nearer: switch
        Assert.Equal(2, h.Hunts);
        Assert.Equal(2, h.Pick(Vector3.Zero, [E(1, 40), E(2, 20)]));
        Assert.Equal(2, h.Hunts);
    }

    [Fact]
    public void A_target_that_goes_into_its_room_is_dropped_and_ending_a_hunt_counts_the_next_as_new()
    {
        var h = new BotHunt();
        Assert.Equal(1, h.Pick(Vector3.Zero, [E(1, 10), E(2, 50)]));
        Assert.Equal(2, h.Pick(Vector3.Zero, [E(1, 10, room: true), E(2, 50)]));
        h.End();
        Assert.Equal(2, h.Pick(Vector3.Zero, [E(2, 50)]));
        Assert.Equal(3, h.Hunts);
    }

    [Fact]
    public void Of_three_bots_on_a_point_exactly_the_highest_id_hunts_and_two_stay()
    {
        int hunting = 0;
        foreach (int id in new[] { 1, 2, 3 })
        {
            var s = new BotSeizeSurplus();
            int lower = new[] { 1, 2, 3 }.Count(o => o < id);
            if (s.Step(0, lower, threat: true)) { hunting++; Assert.Equal(3, id); }
        }
        Assert.Equal(1, hunting);
        // Two on the point: nobody leaves; four: the two highest ids.
        Assert.False(new BotSeizeSurplus().Step(0, 1, true));
        Assert.True(new BotSeizeSurplus().Step(0, 2, true));
        Assert.True(new BotSeizeSurplus().Step(0, 3, true));
    }

    [Fact]
    public void The_surplus_hunt_lasts_at_least_the_latch_and_drops_at_once_when_the_threat_is_gone()
    {
        var s = new BotSeizeSurplus();
        Assert.False(s.Step(0, 5, threat: false));
        Assert.True(s.Step(1, 2, true));
        Assert.True(s.Step(1 + BotSeizeSurplus.Latch - 0.5, 0, true));    // teammates changed: the latch holds
        Assert.False(s.Step(5, 2, threat: false));                         // threat gone: back to the point at once
        Assert.False(s.Step(5.1, 1, true));                                // and no longer surplus
        Assert.True(s.Step(6, 2, true));
        Assert.Equal(2, s.Starts);
        s.End();
        Assert.False(s.Step(6.5, 1, true));
    }
}
