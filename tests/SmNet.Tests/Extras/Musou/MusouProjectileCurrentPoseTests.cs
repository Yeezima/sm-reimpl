using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Codex review 2026-11-21, owner 2026-11-22 ("current position only"): a moving projectile (a rokkit, a launcher or plasma round) tests each Ork's CURRENT pose; only an instant (hit-scan) shot keeps
/// the lag-compensation trail of the last 0.2 s (<see cref="MusouSim.Raycast(Vector3, Vector3, float, out float, out bool, ReadOnlySpan{ushort})"/> vs <see cref="MusouSim.RaycastCurrent"/>).
/// Deterministic, synthetic: an Ork is spawned at A, a tick is stamped into its trail, then it is moved sideways to B (the Codex near miss: the current body misses, the old body would hit).
/// </summary>
public class MusouProjectileCurrentPoseTests
{
    static readonly List<MusouAttack> Attacks = new();

    /// <summary>An Ork stamped at (0, y, 0) on tick 10, then moved to (side, y, 0); the sim is on tick 11 and frozen (the Orks hold still).</summary>
    static (MusouSim Sim, ushort Id) Moved(float y, float side)
    {
        var sim = new MusouSim(null) { Frozen = true };
        ushort id = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, y, 0f), 0f, 100f);
        sim.BeginTick(10);
        sim.Step(ReadOnlySpan<MusouTarget>.Empty, null, false, Attacks);   // the trail now holds (0, y, 0)
        Assert.True(sim.Relocate(id, new Vector3(side, y, 0f)));
        sim.BeginTick(11);
        return (sim, id);
    }

    static float Side(MusouSim sim) => sim.ShotBody(MusouEnemyType.Boy).Radius + 0.3f;   // outside the current body, inside the 1.5 m trail reach

    [Fact]
    public void A_projectile_misses_where_the_ork_was_but_a_hit_scan_shot_still_hits_it()
    {
        var (sim, id) = Moved(10f, 1f);
        var from = new Vector3(0f, 0f, 1f);
        Assert.Equal(id, sim.Raycast(from, Vector3.UnitY, 30f, out float d, out _));   // instant: the trail's old body
        Assert.InRange(d, 5f, 10f);
        Assert.Equal((ushort)0, sim.RaycastCurrent(from, Vector3.UnitY, 30f, out _, out _));   // flying: only where it is now
    }

    [Fact]
    public void The_codex_near_miss_at_the_edge_of_the_body_now_misses_for_a_projectile()
    {
        var (sim, id) = Moved(10f, 0f);
        float r = sim.ShotBody(MusouEnemyType.Boy).Radius;
        Assert.True(sim.Relocate(id, new Vector3(r + 0.1f, 10f, 0f)));
        var from = new Vector3(0f, 0f, 1f);
        Assert.Equal(id, sim.Raycast(from, Vector3.UnitY, 30f, out _, out _));
        Assert.Equal((ushort)0, sim.RaycastCurrent(from, Vector3.UnitY, 30f, out _, out _));
    }

    [Fact]
    public void A_projectile_hits_the_current_body()
    {
        var (sim, id) = Moved(10f, 1f);
        var from = new Vector3(1f, 0f, 1f);   // at the Ork's new x
        Assert.Equal(id, sim.RaycastCurrent(from, Vector3.UnitY, 30f, out float d, out bool head));
        Assert.InRange(d, 5f, 10f);
        Assert.False(head);
    }

    [Fact]
    public void The_first_ork_on_the_way_is_hit_not_a_farther_one()
    {
        var sim = new MusouSim(null);
        ushort far = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 14f, 0f), 0f, 100f);
        ushort near = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 8f, 0f), 0f, 100f);
        sim.BeginTick(10);
        var from = new Vector3(0f, 0f, 1f);
        Assert.Equal(near, sim.RaycastCurrent(from, Vector3.UnitY, 30f, out float d, out _));
        Assert.InRange(d, 6f, 8f);
        Assert.NotEqual(far, near);
        Span<ushort> past = [near];
        Assert.Equal(far, sim.RaycastCurrent(from, Vector3.UnitY, 30f, out _, out _, past));
    }

    static CollisionWorld Wall(float y)
    {
        var v = new List<Vector3> { new(-5, y, -1), new(5, y, -1), new(5, y, 30), new(-5, y, 30) };
        var mesh = new StaticMesh(v.ToArray(), [0, 1, 2, 0, 2, 3]);
        return new CollisionWorld(mesh, mesh);
    }

    [Fact]
    public void A_rokkit_bursts_on_the_wall_in_front_of_an_ork_and_flies_past_the_spot_he_left()
    {
        // Wall in front: the rokkit bursts on the wall, not on the Ork behind it.
        var sim = new MusouSim(null) { Frozen = true };
        ushort shooter = sim.Spawn(MusouEnemyType.RokkitBoy, new Vector3(30f, 0f, 0f), 0f, 100f);
        sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 10f, 0f), 0f, 100f);
        sim.BeginTick(10);
        sim.LaunchRocket(shooter, new Vector3(0f, 20f, 1f), new Vector3(0f, 6.5f, 1f));
        var wall = Wall(7f);
        for (uint t = 11; t < 40 && sim.Rockets.Count > 0; t++) { sim.BeginTick(t); sim.Step(ReadOnlySpan<MusouTarget>.Empty, wall, false, Attacks); }
        var blasts = new List<MusouRocketBlast>(); sim.TakeRocketBlasts(blasts);
        Assert.Single(blasts);
        Assert.InRange(blasts[0].Point.Y, 6f, 7.1f);

        // No wall: the Ork stood on the line a tick ago but has stepped aside; the rokkit does not burst on the old spot.
        var (moved, _) = Moved(10f, Side(sim));
        ushort s2 = moved.Spawn(MusouEnemyType.RokkitBoy, new Vector3(30f, 0f, 0f), 0f, 100f);
        moved.LaunchRocket(s2, new Vector3(0f, 20f, 1f), new Vector3(0f, 9.9f, 1f));
        for (uint t = 11; t < 14; t++) { moved.BeginTick(t); moved.Step(ReadOnlySpan<MusouTarget>.Empty, null, false, Attacks); }
        var after = new List<MusouRocketBlast>(); moved.TakeRocketBlasts(after);
        Assert.Empty(after);
    }
}
