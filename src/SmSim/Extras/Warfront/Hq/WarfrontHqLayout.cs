using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront.Hq;

/// <summary>The HQ defense's places as a battle layout (slice W13b): the chamber as the landing zone, W, K and V as the three positions with the build's markers' radii and half heights, V as the Hq, the build's sources in stage order.</summary>
public static class WarfrontHqLayout
{
    static readonly Lazy<WarfrontHqBuild> Cached = new(WarfrontHqMap.Build);

    /// <summary>The level (built once per process; deterministic).</summary>
    public static WarfrontHqBuild Build => Cached.Value;

    /// <summary>A fresh layout (its radii array is the battle's own: a fallen position's ring is closed in it).</summary>
    public static TfwLayout Layout()
    {
        var b = Build;
        var pts = b.Level.Points;
        Vector3[] positions = [pts[0].Center, pts[1].Center, pts[2].Center];
        float[] radii = [pts[0].Radius, pts[1].Radius, pts[2].Radius];
        float[] heights = [pts[0].HalfHeight, pts[1].HalfHeight, pts[2].HalfHeight];
        return new TfwLayout(b.LandingZone, positions, b.V, [.. b.Sources.OrderBy(s => s.Stage).Select(s => s.Position)], radii, heights);
    }

    /// <summary>The real radius of position <paramref name="k"/> (0 W, 1 K, 2 V) of the map.</summary>
    public static float Radius(int k) => Build.Level.Points[k].Radius;

    /// <summary>The sources of defence stage <paramref name="stage"/> (0 the assembly ground, 1 the courtyard, 2 the hall).</summary>
    public static IEnumerable<Vector3> SourcesOf(int stage) => Build.Sources.Where(s => s.Stage == stage).Select(s => s.Position);

    /// <summary>The posts of defence position <paramref name="position"/>.</summary>
    public static IEnumerable<Vector3> PostsOf(int position) => Build.Posts.Where(p => p.DefencePosition == position).Select(p => p.Position);
}
