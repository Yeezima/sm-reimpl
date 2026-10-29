using System;
using System.Numerics;

namespace SmClient.Bullets;

/// <summary>
/// The engine-free geometry of the visible bullets (branch feel/bullets; <c>NetworkView.Bullets.cs</c> only calls these, tests link this file, no Godot types here).
/// A hit-scan shot's damage goes along one straight ray: the shot origin (the eye moved by the input's origin offset, which for a human is the point
/// of the camera ray nearest the eye) and the shot direction (the view turned by the convergence offsets and the scatter). The drawn bullet leaves the
/// weapon's muzzle, which is not on that ray: it flies from the muzzle to a join point on the ray a few metres on, then along the ray to where the
/// shot ended, so everything past the join point is exactly the damaging path. All positions game space (or any one space), metres.
/// </summary>
public static class BulletPaths
{
    /// <summary>
    /// How far past the muzzle's own distance along the ray the bullet meets it, metres (ours). The muzzle sits a few tenths of a metre off the ray, so
    /// 3 m bends the path by about 2 to 6 degrees at the join and puts the bullet on the ray before anything a close fight can show.
    /// </summary>
    public const float ConvergeDistance = 3f;

    /// <summary>A muzzle closer than this to the ray flies straight to the end (it is on the ray already), metres.</summary>
    public const float OnRay = 0.02f;

    /// <summary>
    /// Where the bullet from <paramref name="muzzle"/> meets the damaging ray (<paramref name="origin"/>, unit <paramref name="direction"/>) on its way to
    /// <paramref name="end"/> (a point of the ray), or null when it flies muzzle to end directly: the muzzle is on the ray, or the end is not
    /// beyond the join point (a target within a few metres: the straight line then differs from the ray by less than the muzzle's offset).
    /// </summary>
    public static Vector3? JoinPoint(Vector3 muzzle, Vector3 origin, Vector3 direction, Vector3 end)
    {
        if (!Finite(muzzle) || !Finite(origin) || !Finite(direction) || !Finite(end)) return null;
        float lengthSquared = direction.LengthSquared();
        if (lengthSquared < 1e-8f) return null;
        direction /= MathF.Sqrt(lengthSquared);
        float alongMuzzle = Vector3.Dot(muzzle - origin, direction);
        float alongEnd = Vector3.Dot(end - origin, direction);
        float offRay = Vector3.Distance(muzzle, origin + direction * alongMuzzle);
        if (offRay < OnRay) return null;
        float join = MathF.Max(alongMuzzle, 0f) + ConvergeDistance;
        // The end must lie clearly beyond the join point, else the muzzle-to-end line is the bullet (ends within the convergence distance).
        if (join > alongEnd - 0.5f) return null;
        return origin + direction * join;
    }

    /// <summary>
    /// The origin a shot of a player seen from outside had: the point of the camera ray (through <paramref name="camera"/> along the unit
    /// <paramref name="view"/>) nearest the <paramref name="eye"/> (the camera itself when it is in front of the eye's plane), its offset from the eye cut
    /// to <paramref name="maxOffset"/> in the same direction, as the server cuts it (<c>Combat.ShotOrigin</c>). The real origin is not on the wire (shot
    /// records carry the end only); this is the rule the shooter's client applied to its own camera.
    /// </summary>
    public static Vector3 OriginOnCameraRay(Vector3 camera, Vector3 view, Vector3 eye, float maxOffset)
    {
        if (!Finite(camera) || !Finite(view) || !Finite(eye) || view.LengthSquared() < 1e-8f) return eye;
        view = Vector3.Normalize(view);
        float t0 = MathF.Max(0f, Vector3.Dot(eye - camera, view));
        var start = camera + view * t0;
        var offset = start - eye;
        float length = offset.Length();
        float max = float.IsFinite(maxOffset) ? MathF.Max(0f, maxOffset) : 0f;
        if (length > max && length > 1e-6f) offset *= max / length;
        return eye + offset;
    }

    /// <summary>The unit direction of the damaging ray of a shot seen from outside: from the reconstructed origin to the end (the end is on the ray), the view when the two coincide.</summary>
    public static Vector3 DirectionTo(Vector3 origin, Vector3 end, Vector3 view)
    {
        var d = end - origin;
        if (!Finite(d) || d.LengthSquared() < 0.09f) return view.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(view);
        return Vector3.Normalize(d);
    }

    /// <summary>The smallest distance from <paramref name="point"/> to the polyline <paramref name="a"/> - <paramref name="b"/> (- <paramref name="c"/> when given): how far a path is from a point of the ray.</summary>
    public static float DistanceToPath(Vector3 point, Vector3 a, Vector3 b, Vector3? c = null)
    {
        float best = DistanceToSegment(point, a, b);
        if (c is { } cc) best = MathF.Min(best, DistanceToSegment(point, b, cc));
        return best;
    }

    /// <summary>The distance from a point to the segment <paramref name="a"/> - <paramref name="b"/>.</summary>
    public static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        float t = l2 < 1e-12f ? 0f : Math.Clamp(Vector3.Dot(p - a, ab) / l2, 0f, 1f);
        return Vector3.Distance(p, a + ab * t);
    }

    /// <summary>The angle in degrees between two directions.</summary>
    public static float AngleDegrees(Vector3 a, Vector3 b)
    {
        float la = a.Length(), lb = b.Length();
        if (la < 1e-8f || lb < 1e-8f) return 0f;
        return MathF.Acos(Math.Clamp(Vector3.Dot(a, b) / (la * lb), -1f, 1f)) * (180f / MathF.PI);
    }

    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
