using System.Numerics;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>Chaos Warfront C5c (Fable review of the merged Chaos line): support is refused during the relay; the Lord's bolt needs a clear line to the duelist.</summary>
public class DuelReviewTests
{
    [Fact]
    public void An_orbital_strike_is_refused_during_the_relay_and_allowed_before_it()
    {
        // before the relay (the bodyguard fight): the call is the run's own
        var (before, _) = FinaleRig.Fortress();
        Assert.False(before.WarfrontDuelRefusesSupport);
        // in the relay: refused, whoever calls it (the duelist included); no cooldown starts, so nothing was fired
        var (w, b, _) = FinaleRig.Duel();
        Assert.True(w.WarfrontDuelRefusesSupport);
        Assert.True(w.Musou!.TryGet(b.BossId, out var lord));
        Assert.True(w.Tfw!.Enqueue(FinaleRig.Human, new TfwCommandRecord(40, TfwCommand.Orbital, 0, lord.Position)));
        Assert.True(w.Tfw.Enqueue(FinaleRig.Human, new TfwCommandRecord(41, TfwCommand.DropPod, 0, lord.Position)));
        FinaleRig.Steps(w, 3);
        Assert.Equal(0u, w.Tfw.Fire.OrbitalTicksLeft(FinaleRig.Human, w.Tick));
    }

    /// <summary>A collision world with one wall: x 5 m, y -20..20 m, z 0..6 m (both windings, both meshes).</summary>
    static CollisionWorld Wall()
    {
        Vector3[] v = [new(5f, -20f, 0f), new(5f, 20f, 0f), new(5f, 20f, 6f), new(5f, -20f, 6f)];
        int[] i = [0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2];
        return new CollisionWorld(new StaticMesh(v, i), new StaticMesh(v, i));
    }

    [Fact]
    public void The_lords_bolt_is_blocked_by_a_wall_and_lands_in_the_open()
    {
        var wall = Wall();
        var from = new Vector3(0f, 0f, 2f);
        Assert.False(World.DuelBoltSees(wall, from, new Vector3(12f, 0f, 0f)));   // the duelist behind the wall
        Assert.True(World.DuelBoltSees(wall, from, new Vector3(4f, 3f, 0f)));     // short of it
        Assert.True(World.DuelBoltSees(wall, from, new Vector3(-12f, 0f, 0f)));   // the other way
        Assert.True(World.DuelBoltSees(null, from, new Vector3(12f, 0f, 0f)));    // no collision: it reaches
    }
}
