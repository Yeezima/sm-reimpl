using System.Numerics;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

namespace SmNet.Tests.Extras.Warfront.Guards;

/// <summary>Guardsmen G1d: the cramped test and the firing positions' assignment (the graph search runs on the real maps: smtool warfront-guards).</summary>
public class WarfrontFiringTests
{
    [Fact]
    public void A_point_is_cramped_when_half_its_slots_lost_half_their_forward_offset()
    {
        Assert.True(TfwBattle.CrampedSlot(0.5f));
        Assert.True(TfwBattle.CrampedSlot(0f));
        Assert.False(TfwBattle.CrampedSlot(0.75f));
        Assert.True(TfwBattle.Cramped(3, 6));
        Assert.False(TfwBattle.Cramped(2, 6));
        Assert.False(TfwBattle.Cramped(0, 0));
        Assert.Equal(0.5f, WarfrontLineRules.CrampedShare);
    }

    [Fact]
    public void Squads_take_the_nearest_positions_in_rank_order_six_metres_apart()
    {
        var slots = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(30, 0, 0) };
        var candidates = new[] { new Vector3(0.5f, 0, 0), new Vector3(2, 0, 0), new Vector3(8, 0, 0), new Vector3(29, 0, 0) };
        var pick = TfwBattle.AssignFiringForTests(slots, candidates);
        Assert.Equal(0, pick[0]);   // the first squad takes the nearest
        Assert.Equal(2, pick[1]);   // the second: 2 m is too near the first's, so 8 m
        Assert.Equal(3, pick[2]);
        for (int i = 0; i < pick.Length; i++)
            for (int j = i + 1; j < pick.Length; j++)
                Assert.True(Vector3.Distance(candidates[pick[i]], candidates[pick[j]]) >= WarfrontLineRules.FiringSpacing);
    }

    [Fact]
    public void A_squad_with_no_position_left_keeps_its_slot()
    {
        var slots = new[] { Vector3.Zero, new Vector3(1, 0, 0) };
        var pick = TfwBattle.AssignFiringForTests(slots, [new Vector3(0, 1, 0)]);
        Assert.Equal(0, pick[0]);
        Assert.Equal(-1, pick[1]);
    }
}
