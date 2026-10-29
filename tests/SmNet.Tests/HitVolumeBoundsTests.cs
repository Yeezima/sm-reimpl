using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// perf/pass-1: <see cref="HitVolumes.Bounds"/> takes each volume's margin once instead of once per corner. The result must be bit-identical to the
/// old formula (below, copied from before the change, the margin per corner) for every volume kind, and for hulls with no vertices or no planes.
/// </summary>
public class HitVolumeBoundsTests
{
    /// <summary>The old <c>HitVolumes.Margin</c> (private there; same code).</summary>
    static float OldMargin(HitVolume v)
    {
        switch (v.Kind)
        {
            case HitVolumeKind.Box: return 0f;
            case HitVolumeKind.Hull:
            {
                float margin = 0f;
                foreach (var p in v.Vertices ?? [])
                {
                    float outermost = float.MinValue;
                    foreach (var q in v.Planes ?? []) outermost = MathF.Max(outermost, q.X * p.X + q.Y * p.Y + q.Z * p.Z + q.W);
                    if (outermost != float.MinValue) margin = MathF.Max(margin, -outermost);
                }
                return margin;
            }
            default: return v.Radius;
        }
    }

    /// <summary>The old <c>Bounds</c> body: the margin taken again for every corner.</summary>
    static (Vector3 Centre, float Radius) OldBounds(IReadOnlyList<HitVolume> list)
    {
        var (bottom, top) = HitVolumes.Extent(list);
        var centre = new Vector3(0, 0, (bottom + top) * 0.5f);
        float radius = 0f;
        foreach (var v in list)
            foreach (var p in HitVolumes.Corners(v))
                radius = MathF.Max(radius, Vector3.Distance(p, centre) + OldMargin(v));
        return (centre, radius + 1e-3f);
    }

    /// <summary>A hull a little inside its vertices (so the margin is not zero): a box of half size 0.3 whose planes sit 0.05 outside it.</summary>
    static (Vector4[] Planes, Vector3[] Vertices) HullData(Vector3 c)
    {
        var planes = new[] { new Vector4(1, 0, 0, -(c.X + 0.35f)), new Vector4(-1, 0, 0, c.X - 0.35f), new Vector4(0, 1, 0, -(c.Y + 0.35f)),
                             new Vector4(0, -1, 0, c.Y - 0.35f), new Vector4(0, 0, 1, -(c.Z + 0.35f)), new Vector4(0, 0, -1, c.Z - 0.35f) };
        var vertices = new List<Vector3>();
        for (int i = 0; i < 8; i++) vertices.Add(c + new Vector3((i & 1) == 0 ? -0.3f : 0.3f, (i & 2) == 0 ? -0.3f : 0.3f, (i & 4) == 0 ? -0.3f : 0.3f));
        return (planes, vertices.ToArray());
    }

    public static IEnumerable<object[]> Cases()
    {
        var (planes, vertices) = HullData(new Vector3(0.02f, -0.05f, 1.6f));
        yield return ["sphere", new List<HitVolume> { HitVolume.Sphere("Head", HitZone.Head, new Vector3(0f, -0.038f, 2.133f), 0.178f) }];
        yield return ["capsule", new List<HitVolume> { HitVolume.Capsule("Arm", HitZone.Body, new Vector3(0.3f, 0.1f, 1.5f), new Vector3(0.45f, -0.2f, 1.1f), 0.09f) }];
        yield return ["box", new List<HitVolume> { HitVolume.Box("Chest", HitZone.Body, new Vector3(0.005f, -0.063f, 1.787f), new Vector3(0.347f, 0.343f, 0.378f), new Quaternion(0.046f, -0.706f, -0.046f, 0.706f)) }];
        yield return ["hull", new List<HitVolume> { HitVolume.Hull("Torso", HitZone.Body, planes, vertices) }];
        yield return ["hull, null vertices", new List<HitVolume> { new("Torso", HitZone.Body, HitVolumeKind.Hull, new Vector3(0, 0, 1.5f), Vector3.Zero, 0f, Quaternion.Identity, planes, null),
                                                                   HitVolume.Sphere("Head", HitZone.Head, new Vector3(0f, 0f, 2.1f), 0.18f) }];
        yield return ["hull, empty planes", new List<HitVolume> { HitVolume.Hull("Torso", HitZone.Body, [], vertices) }];
        yield return ["mixed", new List<HitVolume>(HitVolumeTests.OriginalLike) { HitVolume.Hull("Pad", HitZone.Body, planes, vertices) }];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Bounds_are_bit_identical_to_the_per_corner_margin(string name, List<HitVolume> volumes)
    {
        var (oldCentre, oldRadius) = OldBounds(volumes);
        var (centre, radius) = HitVolumes.Bounds(volumes.ToList());   // a fresh list: Bounds caches per list instance
        Assert.True(BitConverter.SingleToInt32Bits(oldRadius) == BitConverter.SingleToInt32Bits(radius), $"{name}: radius {radius:R} vs old {oldRadius:R}");
        Assert.Equal(oldCentre, centre);
    }
}
