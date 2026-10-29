using System.Numerics;

namespace SmSim;

/// <summary>The kinds of hit volume: sphere, capsule, oriented box, convex hull.</summary>
public enum HitVolumeKind : byte { Sphere = 0, Capsule = 1, Box = 2, Hull = 3 }

/// <summary>
/// One hit volume in the character frame (metres; X right, Y forward, Z up, feet at the origin, as the model faces +Y); the
/// world turns it by the player's yaw about the feet. Sphere: centre <see cref="A"/>, <see cref="Radius"/>. Capsule: segment
/// <see cref="A"/>–<see cref="B"/>, <see cref="Radius"/>. Box: centre <see cref="A"/>, half extents <see cref="B"/> along its
/// own axes, which <see cref="Rotation"/> turns into the character frame (<see cref="Radius"/> unused, 0). Hull: the points
/// with n·p + w ≤ 0 for every plane in <see cref="Planes"/> (unit outward normal xyz, offset w), drawn from
/// <see cref="Vertices"/> (points on or just inside the surface); <see cref="A"/> is the vertices' centroid, <see cref="B"/> 0.
/// </summary>
public sealed record HitVolume(string Name, HitZone Zone, HitVolumeKind Kind, Vector3 A, Vector3 B, float Radius, Quaternion Rotation,
    IReadOnlyList<Vector4>? Planes = null, IReadOnlyList<Vector3>? Vertices = null)
{
    public static HitVolume Sphere(string name, HitZone zone, Vector3 centre, float radius) => new(name, zone, HitVolumeKind.Sphere, centre, centre, radius, Quaternion.Identity);
    public static HitVolume Capsule(string name, HitZone zone, Vector3 a, Vector3 b, float radius) => new(name, zone, HitVolumeKind.Capsule, a, b, radius, Quaternion.Identity);
    public static HitVolume Box(string name, HitZone zone, Vector3 centre, Vector3 halfExtents, Quaternion rotation) =>
        new(name, zone, HitVolumeKind.Box, centre, halfExtents, 0f, Quaternion.Normalize(rotation));
    /// <summary>A convex hull from its planes (inside: n·p + w ≤ 0) and the vertices it is drawn from.</summary>
    public static HitVolume Hull(string name, HitZone zone, IReadOnlyList<Vector4> planes, IReadOnlyList<Vector3> vertices) =>
        new(name, zone, HitVolumeKind.Hull, vertices.Count == 0 ? Vector3.Zero : vertices.Aggregate(Vector3.Add) / vertices.Count, Vector3.Zero, 0f, Quaternion.Identity,
            planes.ToArray(), vertices.ToArray());

    /// <summary>Whether the values are usable: finite, a positive radius (sphere, capsule) or non-negative half extents with one positive (box), a unit rotation.</summary>
    public bool IsValid =>
        Enum.IsDefined(Zone) && Zone != HitZone.None && Enum.IsDefined(Kind) && Finite(A) && Finite(B) && float.IsFinite(Radius)
        && float.IsFinite(Rotation.X) && float.IsFinite(Rotation.Y) && float.IsFinite(Rotation.Z) && float.IsFinite(Rotation.W) && MathF.Abs(Rotation.Length() - 1f) < 1e-3f
        && Kind switch
        {
            HitVolumeKind.Box => B.X >= 0 && B.Y >= 0 && B.Z >= 0 && MathF.Max(B.X, MathF.Max(B.Y, B.Z)) > 0,
            HitVolumeKind.Hull => Planes is { Count: >= 4 } planes && Vertices is { Count: >= 4 } vertices
                && planes.All(q => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W) && MathF.Abs(new Vector3(q.X, q.Y, q.Z).Length() - 1f) < 1e-3f)
                && vertices.All(Finite),
            _ => Radius > 0,
        };

    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

/// <summary>
/// Hit-volume geometry (M20): rays, containment, bounds and sample points of a player's volumes standing at its feet and
/// turned by its yaw. With <see cref="SimConfig.HitVolumes"/> unset, <see cref="Of"/> gives the placeholder head sphere and
/// body capsule built from <see cref="SimConfig.HeadHeight"/> and the body fields (the shapes used before M20).
/// </summary>
public static class HitVolumes
{
    /// <summary>The volumes in use: <see cref="SimConfig.HitVolumes"/>, or the placeholder pair (head sphere, body capsule).</summary>
    public static IReadOnlyList<HitVolume> Of(SimConfig c) => c.HitVolumes ?? Placeholder(c);

    /// <summary>The placeholder volumes: head sphere at <see cref="SimConfig.HeadHeight"/>, body capsule between BodyBottom and BodyTop (outer extents).</summary>
    public static IReadOnlyList<HitVolume> Placeholder(SimConfig c)
    {
        float low = c.BodyBottom + c.BodyRadius, high = MathF.Max(low + 1e-3f, c.BodyTop - c.BodyRadius);
        return
        [
            HitVolume.Sphere("head (placeholder)", HitZone.Head, new Vector3(0, 0, c.HeadHeight), c.HeadRadius),
            HitVolume.Capsule("body (placeholder)", HitZone.Body, new Vector3(0, 0, low), new Vector3(0, 0, high), c.BodyRadius),
        ];
    }

    /// <summary>A world point in the character frame of a player at <paramref name="feet"/> facing <paramref name="yaw"/>.</summary>
    public static Vector3 ToLocal(Vector3 point, Vector3 feet, float yaw) => Rotate(point - feet, -yaw);

    /// <summary>A character-frame point in the world.</summary>
    public static Vector3 ToWorld(Vector3 local, Vector3 feet, float yaw) => Rotate(local, yaw) + feet;

    /// <summary>A vector turned about Z by <paramref name="yaw"/> (yaw 0 keeps +Y forward; positive turns left, as movement).</summary>
    public static Vector3 Rotate(Vector3 v, float yaw)
    {
        if (yaw == 0f) return v;
        float s = MathF.Sin(yaw), co = MathF.Cos(yaw);
        return new Vector3(v.X * co - v.Y * s, v.X * s + v.Y * co, v.Z);
    }

    /// <summary>
    /// The nearest volume a ray (unit <paramref name="direction"/>) enters within <paramref name="maxRange"/>, and the distance;
    /// ties go to the earlier volume in the list. A ray starting inside a volume hits it at 0.
    /// </summary>
    public static (HitZone Zone, float Distance) Raycast(IReadOnlyList<HitVolume> volumes, Vector3 origin, Vector3 direction, Vector3 feet, float yaw, float maxRange)
    {
        var o = ToLocal(origin, feet, yaw);
        var (centre, radius) = Bounds(volumes);
        if (RaySphere(o, Rotate(direction, -yaw), centre, radius) is not { } near || near > maxRange) return (HitZone.None, float.MaxValue);   // misses them all
        var d = Rotate(direction, -yaw);
        var zone = HitZone.None; float best = maxRange; bool found = false;
        foreach (var v in volumes)
        {
            float? t = v.Kind switch
            {
                HitVolumeKind.Sphere => RaySphere(o, d, v.A, v.Radius),
                HitVolumeKind.Capsule => RayCapsule(o, d, v.A, v.B, v.Radius),
                HitVolumeKind.Hull => RayHull(o, d, v.Planes!),
                _ => RayBox(o, d, v),
            };
            if (t is { } hit && (found ? hit < best : hit <= best)) { best = hit; zone = v.Zone; found = true; }
        }
        return (zone, found ? best : float.MaxValue);
    }

    /// <summary>The zone of the first volume containing <paramref name="point"/>, or <see cref="HitZone.None"/>.</summary>
    public static HitZone Contains(IReadOnlyList<HitVolume> volumes, Vector3 point, Vector3 feet, float yaw)
    {
        var p = ToLocal(point, feet, yaw);
        var (centre, radius) = Bounds(volumes);
        if (Vector3.DistanceSquared(p, centre) > radius * radius) return HitZone.None;
        foreach (var v in volumes)
        {
            bool inside = v.Kind switch
            {
                HitVolumeKind.Sphere => Vector3.DistanceSquared(p, v.A) <= v.Radius * v.Radius,
                HitVolumeKind.Capsule => Vector3.DistanceSquared(p, ClosestOnSegment(p, v.A, v.B)) <= v.Radius * v.Radius,
                HitVolumeKind.Hull => v.Planes!.All(q => q.X * p.X + q.Y * p.Y + q.Z * p.Z + q.W <= 0f),
                _ => InsideBox(p, v),
            };
            if (inside) return v.Zone;
        }
        return HitZone.None;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<HitVolume>, Tuple<Vector3, float>> _bounds = new();

    /// <summary>A sphere around all the volumes (character frame; cached per list): centre on the axis at mid height.</summary>
    public static (Vector3 Centre, float Radius) Bounds(IReadOnlyList<HitVolume> volumes)
    {
        var b = _bounds.GetValue(volumes, list =>
        {
            var (bottom, top) = Extent(list);
            var centre = new Vector3(0, 0, (bottom + top) * 0.5f);
            float radius = 0f;
            foreach (var v in list)
            {
                // perf/pass-1: the margin once per volume (it was taken again for every corner: vertices x vertices x planes for a hull, 20 to 45 ms per
                // flight pose set on the client's crosshair ray in a Debug build, a few ms optimised; same result).
                float margin = Margin(v);
                foreach (var p in Corners(v))
                    radius = MathF.Max(radius, Vector3.Distance(p, centre) + margin);
            }
            return Tuple.Create(centre, radius + 1e-3f);
        });
        return (b.Item1, b.Item2);
    }

    /// <summary>
    /// How far a volume reaches beyond its <see cref="Corners"/>: a sphere's or capsule's radius; a hull's convex radius (its
    /// planes lie that far outside its vertices: the largest over vertices of how deep inside the planes it sits); 0 for a box.
    /// </summary>
    static float Margin(HitVolume v)
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

    /// <summary>The lowest and highest points of the volumes above the feet (any yaw).</summary>
    public static (float Bottom, float Top) Extent(IReadOnlyList<HitVolume> volumes)
    {
        float bottom = float.MaxValue, top = float.MinValue;
        foreach (var v in volumes)
            foreach (var p in Corners(v))
            {
                float r = v.Kind is HitVolumeKind.Box or HitVolumeKind.Hull ? 0f : v.Radius;
                bottom = MathF.Min(bottom, p.Z - r); top = MathF.Max(top, p.Z + r);
            }
        return volumes.Count == 0 ? (0f, 0f) : (bottom, top);
    }

    /// <summary>
    /// The height above the feet that blasts, melee and the melta measure to ("chest"): the placeholder body's middle,
    /// (BodyBottom + BodyTop) / 2, or with <see cref="SimConfig.HitVolumes"/> the middle of their extent (interpretation).
    /// </summary>
    public static float CentreHeight(SimConfig c)
    {
        if (c.HitVolumes is not { Count: > 0 } volumes) return (c.BodyBottom + c.BodyTop) * 0.5f;
        var (bottom, top) = Extent(volumes);
        return (bottom + top) * 0.5f;
    }

    /// <summary>Points that stand for the volumes when measuring cone coverage: each centre, and a capsule's ends (character frame).</summary>
    public static IEnumerable<Vector3> SamplePoints(IReadOnlyList<HitVolume> volumes)
    {
        foreach (var v in volumes)
        {
            yield return (v.A + (v.Kind == HitVolumeKind.Capsule ? v.B : v.A)) * 0.5f;
            if (v.Kind == HitVolumeKind.Capsule) { yield return v.A; yield return v.B; }
        }
    }

    /// <summary>The corners of a box, the vertices of a hull, the ends of a capsule, the centre of a sphere (character frame).</summary>
    public static IEnumerable<Vector3> Corners(HitVolume v)
    {
        if (v.Kind == HitVolumeKind.Hull) { foreach (var p in v.Vertices ?? []) yield return p; yield break; }
        if (v.Kind == HitVolumeKind.Sphere) { yield return v.A; yield break; }
        if (v.Kind == HitVolumeKind.Capsule) { yield return v.A; yield return v.B; yield break; }
        for (int i = 0; i < 8; i++)
            yield return v.A + Vector3.Transform(new Vector3((i & 1) == 0 ? -v.B.X : v.B.X, (i & 2) == 0 ? -v.B.Y : v.B.Y, (i & 4) == 0 ? -v.B.Z : v.B.Z), v.Rotation);
    }

    static bool InsideBox(Vector3 p, HitVolume v)
    {
        var q = Vector3.Transform(p - v.A, Quaternion.Conjugate(v.Rotation));
        return MathF.Abs(q.X) <= v.B.X && MathF.Abs(q.Y) <= v.B.Y && MathF.Abs(q.Z) <= v.B.Z;
    }

    static Vector3 ClosestOnSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-12f) return a;
        return a + ab * Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f);
    }

    internal static float? RaySphere(Vector3 o, Vector3 d, Vector3 center, float r)
    {
        var m = o - center;
        float b = Vector3.Dot(m, d), cc = Vector3.Dot(m, m) - r * r;
        if (cc > 0f && b > 0f) return null;
        float disc = b * b - cc;
        if (disc < 0f) return null;
        return MathF.Max(0f, -b - MathF.Sqrt(disc));
    }

    /// Ray against a capsule (segment a–b, radius r): nearest distance along the ray, or null.
    internal static float? RayCapsule(Vector3 o, Vector3 d, Vector3 a, Vector3 b, float r)
    {
        float? best = null;
        void Take(float? t) { if (t is { } v && (best is null || v < best)) best = v; }
        Take(RaySphere(o, d, a, r));
        Take(RaySphere(o, d, b, r));
        var axis = b - a; float len = axis.Length();
        if (len < 1e-6f) return best;
        var u = axis / len;
        var dp = d - Vector3.Dot(d, u) * u;
        var op = (o - a) - Vector3.Dot(o - a, u) * u;
        float A = Vector3.Dot(dp, dp), B = Vector3.Dot(op, dp), C = Vector3.Dot(op, op) - r * r;
        if (A > 1e-9f)
        {
            float disc = B * B - A * C;
            if (disc >= 0f)
            {
                float t = (-B - MathF.Sqrt(disc)) / A;
                if (t < 0f && C <= 0f) t = 0f;   // starts inside
                if (t >= 0f)
                {
                    float along = Vector3.Dot(o + d * t - a, u);
                    if (along >= 0f && along <= len) Take(t);
                }
            }
        }
        return best;
    }

    /// Ray against a convex hull given by its planes (clipping the ray against each); a start inside hits at 0.
    static float? RayHull(Vector3 o, Vector3 d, IReadOnlyList<Vector4> planes)
    {
        float tMin = 0f, tMax = float.MaxValue;
        foreach (var q in planes)
        {
            var n = new Vector3(q.X, q.Y, q.Z);
            float distance = Vector3.Dot(n, o) + q.W, denom = Vector3.Dot(n, d);
            if (MathF.Abs(denom) < 1e-9f)
            {
                if (distance > 0f) return null;   // parallel and outside
                continue;
            }
            float t = -distance / denom;
            if (denom < 0f) tMin = MathF.Max(tMin, t);   // entering through this plane
            else tMax = MathF.Min(tMax, t);              // leaving
            if (tMin > tMax) return null;
        }
        return tMin;
    }

    /// Ray against an oriented box (slab test in the box's frame); a start inside hits at 0.
    static float? RayBox(Vector3 o, Vector3 d, HitVolume v)
    {
        var inverse = Quaternion.Conjugate(v.Rotation);
        var lo = Vector3.Transform(o - v.A, inverse);
        var ld = Vector3.Transform(d, inverse);
        float tMin = 0f, tMax = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float origin = axis == 0 ? lo.X : axis == 1 ? lo.Y : lo.Z;
            float dir = axis == 0 ? ld.X : axis == 1 ? ld.Y : ld.Z;
            float half = axis == 0 ? v.B.X : axis == 1 ? v.B.Y : v.B.Z;
            if (MathF.Abs(dir) < 1e-9f)
            {
                if (MathF.Abs(origin) > half) return null;
                continue;
            }
            float t1 = (-half - origin) / dir, t2 = (half - origin) / dir;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tMin = MathF.Max(tMin, t1); tMax = MathF.Min(tMax, t2);
            if (tMin > tMax) return null;
        }
        return tMin;
    }
}
