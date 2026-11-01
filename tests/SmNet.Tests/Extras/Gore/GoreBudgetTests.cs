using System.Numerics;
using SmNet.Extras.Gore;
using Xunit;

namespace SmNet.Tests.Extras.Gore;

/// <summary>Hotfix 2026-11-01, the gore performance pass: a mass kill stays inside the budgets, and flying blood lands only on a surface.</summary>
public class GoreBudgetTests
{
    [Fact]
    public void A_mass_explosion_kill_makes_few_large_puffs_and_a_bounded_number_of_flying_pieces()
    {
        var f = new GoreField(1);
        var burst = GoreRules.Burst(GoreClass.Explosion, 0, 1f, 5f, 1f, true);
        for (int i = 0; i < 30; i++) f.Spawn(burst, new Vector3(100f + i * 0.1f, 50f, 1f), Vector3.Zero, 0f, true);
        Assert.InRange(f.LiveLargePuffs, 1, GoreField.MaxLargePuffs);
        Assert.True(f.LiveMists <= 40, "30 deaths in one cell make a few banks, not 420 puffs: " + f.LiveMists);
        Assert.True(f.LiveDrops + f.LiveGibs <= GoreField.FlyingPerStep + 30 * (GoreField.MinFlyingPerDeath + 40), "flying pieces: " + (f.LiveDrops + f.LiveGibs));
    }

    [Fact]
    public void Blood_over_a_drop_with_no_surface_is_removed_and_blood_over_ground_stains_it()
    {
        var f = new GoreField(2) { GroundAt = p => null };
        f.Spray(new Vector3(0, 0, 3f), Vector3.UnitY, 20, 1f);
        for (int i = 0; i < 60; i++) f.Step(0.05f);
        Assert.Equal(0, f.LiveDrops);
        Assert.Equal(0, f.LiveSpatters);

        var g = new GoreField(3) { GroundAt = p => -2f };   // the real ground is 2 m below the flat guess
        g.Spray(new Vector3(0, 0, 3f), Vector3.UnitY, 40, 1f);
        for (int i = 0; i < 60; i++) g.Step(0.05f);
        Assert.Equal(0, g.LiveDrops);
        for (int i = 0; i < g.Spatters.Length; i++) if (g.Spatters[i].Live) Assert.Equal(-2f, g.Spatters[i].Pos.Z);
    }
}
