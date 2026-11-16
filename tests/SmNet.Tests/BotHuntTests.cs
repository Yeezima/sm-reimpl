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
    public void Enemies_outside_the_zone_come_first_then_those_in_the_zone_outside_the_room()
    {
        var h = new BotHunt();
        Assert.Equal(3, h.Pick(Vector3.Zero, [E(1, 5, zone: true), E(2, 7, room: true), E(3, 90)]));
        Assert.False(h.InZone);
        var h2 = new BotHunt();
        Assert.Equal(1, h2.Pick(Vector3.Zero, [E(1, 40, zone: true), E(2, 20, room: true), E(4, 60, zone: true)]));
        Assert.True(h2.InZone);
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
}
