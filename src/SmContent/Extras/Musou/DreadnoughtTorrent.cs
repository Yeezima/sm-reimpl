using System.Numerics;

namespace SmContent.Extras.Musou;

/// <summary>
/// The Musou Dreadnought's torrent of fire (owner 2026-11-18: "more tracers spitting in a circular pattern with some spread in visual tracer only ... a torrent of fire leaving the gun"; added content, Musou only, client
/// drawing only: nothing here is read by the simulation, the server or the wire). For each real shot of the assault cannon the client draws <see cref="PerShot"/> extra purely visual streaks leaving the barrel on a ring
/// around the aim axis; the ring turns <see cref="SpinPerShot"/> radians per shot, each streak is tilted outward by <see cref="ConeDegrees"/> and jittered by up to <see cref="JitterDegrees"/>. Every number is **ours**
/// (an estimate to refine in a playtest); the settings are tunable by the client's test aids (<c>--dn-torrent-count</c>, <c>-radius</c>, <c>-cone</c>, <c>-jitter</c>, <c>-spin</c>; <c>--old-dn-torrent</c> restores the single real tracer).
/// </summary>
public static class DreadnoughtTorrent
{
    public const int PerShot = 6;
    public const float RingRadius = 0.45f;       // metres, the ring's radius at the barrel
    public const float ConeDegrees = 2.5f;       // outward tilt of a streak from the aim axis
    public const float JitterDegrees = 1.5f;     // random extra spread
    public const float SpinPerShot = 1.1f;       // radians the ring turns between shots
    public const float StreakSpeed = 350f;       // metres per second of a streak's bright head
    public const float TailLength = 8f;          // metres of fading tail
    public const float MaxLength = 70f;          // a streak never flies further than this or the real shot's length
    public const int MaxPerFrame = 24;           // spawn cap per frame (the pool's guard)
    public const float ShakeScale = 0.2f;        // the Dreadnought player's camera shake x0.2 (owner: reduce by 80 %)

    /// <summary>The tunable settings (defaults above).</summary>
    public readonly record struct Settings(int Count, float Radius, float ConeDeg, float JitterDeg, float Spin)
    {
        public static Settings Default => new(PerShot, RingRadius, ConeDegrees, JitterDegrees, SpinPerShot);
    }

    /// <summary>
    /// The streaks of shot number <paramref name="shot"/>: for each, its start offset from the muzzle (on the ring, in the plane across <paramref name="axis"/>) and its unit direction.
    /// <paramref name="rand"/> returns uniform values in 0..1.
    /// </summary>
    public static (Vector3 Offset, Vector3 Direction)[] Fan(Vector3 axis, int shot, Settings s, Func<float> rand)
    {
        int n = Math.Max(0, s.Count);
        var result = new (Vector3, Vector3)[n];
        if (n == 0 || axis.LengthSquared() < 1e-8f) return result;
        axis = Vector3.Normalize(axis);
        var helper = MathF.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(axis, helper));
        var v = Vector3.Cross(axis, u);
        float tilt = MathF.Tan(s.ConeDeg * MathF.PI / 180f), jitter = MathF.Tan(s.JitterDeg * MathF.PI / 180f);
        for (int i = 0; i < n; i++)
        {
            float phase = shot * s.Spin + i * 2f * MathF.PI / n;
            var radial = u * MathF.Cos(phase) + v * MathF.Sin(phase);
            var offset = radial * (s.Radius * (0.6f + 0.4f * rand()));
            var dir = axis + radial * (tilt * (0.5f + 0.5f * rand())) + u * ((rand() * 2f - 1f) * jitter) + v * ((rand() * 2f - 1f) * jitter);
            result[i] = (offset, Vector3.Normalize(dir));
        }
        return result;
    }
}
