using System.Collections.Generic;
using System.Numerics;
using SmClient.ImpactRules;
using static SmClient.ImpactRules.ClassicBloodRules;

namespace SmNet.Tests;

/// <summary>classic-hit-effects, the Extended profile's fatal gunfire headshot wall decal: when it is placed and the splash it draws.</summary>
public class ClassicBloodRulesTests
{
    static readonly Vector3 Wall = new(-1, 0, 0);   // a wall facing back along a +X shot
    static TraceHit At(float d, Vector3 normal, bool player = false) => new(d, new Vector3(d, 0, 1.7f), normal, player);

    [Theory]
    [InlineData(1.0f)]
    [InlineData(4.99f)]
    [InlineData(5.0f)]
    public void An_aligned_wall_at_or_inside_five_metres_takes_the_decal_at_the_intersection(float d)
    {
        var (r, hit) = DecalTrace(fatal: true, head: true, gunfire: true, [At(d, Wall)]);
        Assert.Equal(DecalResult.Placed, r);
        Assert.Equal(new Vector3(d, 0, 1.7f), hit.Point);
    }

    [Fact]
    public void Past_the_cutoff_absent_out_of_line_blocked_or_not_a_wall_leaves_none()
    {
        Assert.Equal(DecalResult.TooFar, DecalTrace(true, true, true, [At(5.01f, Wall)]).Result);
        Assert.Equal(DecalResult.NoWall, DecalTrace(true, true, true, []).Result);   // nothing on the line: no wall, or a wall only to the side (the ray misses it)
        Assert.Equal(DecalResult.Blocked, DecalTrace(true, true, true, [At(3f, Wall), At(1.2f, -Vector3.UnitX, player: true)]).Result);
        Assert.Equal(DecalResult.NotAWall, DecalTrace(true, true, true, [At(2f, Vector3.UnitZ)]).Result);    // the floor
        Assert.Equal(DecalResult.NotAWall, DecalTrace(true, true, true, [At(2f, -Vector3.UnitZ)]).Result);   // a ceiling
        // the first surface decides: a crate top in front of the wall blocks it (no fallback to the wall behind)
        Assert.Equal(DecalResult.NotAWall, DecalTrace(true, true, true, [At(4f, Wall), At(1f, Vector3.Normalize(new Vector3(-0.3f, 0, 1)))]).Result);
        Assert.True(IsWall(Vector3.Normalize(new Vector3(-1, 0, 0.4f))));   // a slightly leaning wall
    }

    [Fact]
    public void Only_a_fatal_gunfire_headshot_qualifies()
    {
        Assert.Equal(DecalResult.NotFatal, DecalTrace(fatal: false, head: true, gunfire: true, [At(2f, Wall)]).Result);
        Assert.Equal(DecalResult.NotHead, DecalTrace(fatal: true, head: false, gunfire: true, [At(2f, Wall)]).Result);
        Assert.Equal(DecalResult.NotGunfire, DecalTrace(fatal: true, head: true, gunfire: false, [At(2f, Wall)]).Result);
    }

    [Fact]
    public void The_splash_is_the_same_in_every_view_and_varies_between_events()
    {
        Assert.Equal(VariantSeed(12, 4), VariantSeed(12, 4));
        Assert.NotEqual(VariantSeed(12, 4), VariantSeed(13, 4));
        var a = DecalShape(VariantSeed(12, 4), 96);
        var b = DecalShape(VariantSeed(12, 4), 96);
        var c = DecalShape(VariantSeed(13, 4), 96);
        Assert.Equal(a.Alpha, b.Alpha);
        Assert.NotEqual(a.Alpha, c.Alpha);
    }

    [Fact]
    public void The_splash_has_a_dense_dark_core_at_the_centre_clear_corners_and_drips_below()
    {
        const int n = 128;
        float below = 0f, above = 0f;
        for (int seed = 1; seed <= 20; seed++)
        {
            var (alpha, shade) = DecalShape(seed, n);
            int centre = n / 2 * n + n / 2;
            Assert.True(alpha[centre] > 0.99f, $"seed {seed}: core");
            Assert.True(shade[centre] < 0.7f, $"seed {seed}: the core is darker");
            Assert.True(alpha[0] < 0.01f && alpha[n - 1] < 0.01f && alpha[(n - 1) * n] < 0.01f, $"seed {seed}: corners clear");
            // thin vertical bands outside the lobes' main reach: the drips run down, nothing runs up
            for (int y = (int)(n * 0.84); y < (int)(n * 0.97); y++) for (int x = (int)(n * 0.3); x < (int)(n * 0.7); x++) below += alpha[y * n + x];
            for (int y = (int)(n * 0.03); y < (int)(n * 0.16); y++) for (int x = (int)(n * 0.3); x < (int)(n * 0.7); x++) above += alpha[y * n + x];
        }
        Assert.True(below > above, $"drips below {below:F0} vs above {above:F0}");
    }
}
