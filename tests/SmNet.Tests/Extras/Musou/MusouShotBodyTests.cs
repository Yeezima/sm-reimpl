using System.Numerics;
using SmSim.Extras.Musou;
using Xunit;

namespace SmNet.Tests.Extras.Musou;

/// <summary>The enemies' bodies for shots: the drawn height, closed at the top, and the recent path.</summary>
public class MusouShotBodyTests
{
    static MusouSim Sim() => new(null, new MusouRules());

    [Theory]
    [InlineData(1.9f)]
    [InlineData(1.7f)]
    public void A_flat_shot_at_a_boys_upper_body_hits(float z)
    {
        var sim = Sim();
        ushort boy = sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, 85f);
        Assert.Equal(boy, sim.Raycast(new Vector3(0f, -10f, z), Vector3.UnitY, 30f, out _, out _));
    }

    [Fact]
    public void A_nobs_top_third_is_there()
    {
        var sim = Sim();
        ushort nob = sim.Spawn(MusouEnemyType.Nob, Vector3.Zero, 0f, 600f);
        Assert.Equal(nob, sim.Raycast(new Vector3(0f, -10f, 2.7f), Vector3.UnitY, 30f, out _, out _));
    }

    [Fact]
    public void A_steep_shot_through_the_shoulders_hits()
    {
        var sim = Sim();
        ushort boy = sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, 85f);
        var from = new Vector3(0.1f, 0f, 6f);
        Assert.Equal(boy, sim.Raycast(from, Vector3.Normalize(new Vector3(0f, 0f, -1f)), 10f, out _, out _));
    }

    [Fact]
    public void A_shot_well_beside_an_enemy_misses()
    {
        var sim = Sim();
        sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, 85f);
        Assert.Equal(0, sim.Raycast(new Vector3(2.5f, -10f, 1f), Vector3.UnitY, 30f, out _, out _));
    }
}
