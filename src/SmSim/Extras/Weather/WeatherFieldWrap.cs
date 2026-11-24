using System;
using System.Numerics;

namespace SmSim.Extras.Weather;

/// <summary>
/// Rain motion (series 37; presentation only, client only, **ours**; pure, no engine types): the precipitation layers (near and far rain, the Ash Squall's grit and flakes, Ember Wind's embers) are one fixed set of drops in a
/// world-anchored field that repeats every <c>box</c> metres on each axis; each frame the field moves by its fall (or wind) velocity times the frame (<see cref="Advance"/>), and every drop is drawn at the one copy of itself that lies
/// inside the box centred on the camera (<see cref="Place"/>). A drop is therefore a fixed point in the world between frames (the camera's own motion gives true parallax and never leaves the rain behind), and the box round the
/// camera always holds every drop exactly once: the density round the camera is the same at any speed in any direction, up and down included (the emitters before series 37 spawned drops at a height over the camera and let them
/// fall, so a camera falling with or faster than the rain outran them). A drop that leaves the box by one face comes back through the opposite face; <see cref="EdgeFade"/> hides it there. The client's streak and dot shaders do the
/// same arithmetic per vertex; this class is their reference and the density aid's.
/// </summary>
public static class WeatherFieldWrap
{
    /// <summary>The share of each half-box next to a face over which a drop fades out (0 at the face), so a drop never pops where it wraps.</summary>
    public const float EdgeShare = 0.15f;

    /// <summary>The field's offset after <paramref name="dt"/> s at <paramref name="velocity"/> m/s, kept inside [0, box) on each axis (the field repeats every box, so this loses nothing and keeps the numbers small).</summary>
    public static Vector3 Advance(Vector3 drift, Vector3 velocity, double dt, Vector3 box)
    {
        if (!(dt > 0) || !double.IsFinite(dt)) dt = 0;
        dt = Math.Min(dt, 0.25);   // a stall moves the field at most a quarter second
        return new Vector3(Mod(drift.X + (double)velocity.X * dt, box.X), Mod(drift.Y + (double)velocity.Y * dt, box.Y), Mod(drift.Z + (double)velocity.Z * dt, box.Z));
    }

    /// <summary>Where a drop with seed <paramref name="seed01"/> (each axis in [0, 1)) is drawn this frame: its world point (seed times box plus the drift, repeated every box) inside the box of size <paramref name="box"/> centred on <paramref name="centre"/>.</summary>
    public static Vector3 Place(Vector3 seed01, Vector3 box, Vector3 drift, Vector3 centre)
    {
        var lo = centre - box * 0.5f;
        return lo + new Vector3(Mod((double)seed01.X * box.X + drift.X - lo.X, box.X), Mod((double)seed01.Y * box.Y + drift.Y - lo.Y, box.Y), Mod((double)seed01.Z * box.Z + drift.Z - lo.Z, box.Z));
    }

    /// <summary>The drop's alpha factor by its place in the box: 1 inside, falling smoothly to 0 at each face over <see cref="EdgeShare"/> of the half-box.</summary>
    public static float EdgeFade(Vector3 at, Vector3 centre, Vector3 box)
    {
        return Axis(at.X - centre.X, box.X) * Axis(at.Y - centre.Y, box.Y) * Axis(at.Z - centre.Z, box.Z);
        static float Axis(float d, float size)
        {
            if (!(size > 0)) return 0f;
            float q = Math.Abs(d) / (size * 0.5f);   // 0 at the centre, 1 at the face
            return 1f - SmoothStep(1f - EdgeShare, 1f, q);
        }
    }

    /// <summary>The far rain's ring under cover: 0 within <paramref name="holeM"/> of the centre (horizontally), 1 from 2 m further, and 0 again beyond <paramref name="ringM"/>; a hole of 0 or less keeps every drop.</summary>
    public static float HoleFade(Vector3 at, Vector3 centre, float holeM, float ringM)
    {
        if (!(holeM > 0)) return 1f;
        float r = MathF.Sqrt((at.X - centre.X) * (at.X - centre.X) + (at.Z - centre.Z) * (at.Z - centre.Z));
        return SmoothStep(holeM, holeM + 2f, r) * (1f - SmoothStep(ringM - 2f, ringM, r));
    }

    static float Mod(double v, float size) => size > 0 ? (float)(v - Math.Floor(v / size) * size) : 0f;

    static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
