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

    /// <summary>
    /// The far rain's ring under cover: 0 within 2 m short of <paramref name="holeM"/> of the centre (horizontally), 1 from <paramref name="holeM"/>, and 0 again beyond <paramref name="ringM"/> (from 2 m short of it). A hole of 0 and a ring
    /// past the box's corners keep every drop, so the rig eases both radii (<see cref="Ease"/>) instead of switching the ring on and off.
    /// </summary>
    public static float HoleFade(Vector3 at, Vector3 centre, float holeM, float ringM)
    {
        float r = MathF.Sqrt((at.X - centre.X) * (at.X - centre.X) + (at.Z - centre.Z) * (at.Z - centre.Z));
        return SmoothStep(holeM - 2f, holeM, r) * (1f - SmoothStep(ringM - 2f, ringM, r));
    }

    /// <summary>The seconds a field takes to fade fully in or out when it is switched, and a hole or ring radius to move from one value to the other (repair: no layer or ring pops).</summary>
    public const double FadeSeconds = 0.5;

    /// <summary><paramref name="now"/> moved toward <paramref name="target"/> by at most <paramref name="span"/> x dt / <see cref="FadeSeconds"/> (a full change takes <see cref="FadeSeconds"/>).</summary>
    public static float Ease(float now, float target, float span, double dt)
    {
        if (!(dt > 0) || !double.IsFinite(dt)) return now;
        float step = (float)(Math.Abs(span) * Math.Min(dt, 0.25) / FadeSeconds);
        if (Math.Abs(target - now) <= step + 1e-5f) return target;   // lands exactly (no float residue keeps a fade alive)
        return now < target ? Math.Min(target, now + step) : Math.Max(target, now - step);
    }

    /// <summary>
    /// Repair: drift groups for the soft flakes and the embers (a field moving as one reads as a rigid sheet). A drop's group scales the layer's velocity by <see cref="GroupSpeed"/> and turns it about the vertical by <see cref="GroupYawDeg"/>;
    /// each group has its own drift, so the wrap stays exact. The speeds average 1.
    /// </summary>
    public static readonly float[] GroupSpeed = { 0.82f, 0.94f, 1.06f, 1.18f };
    public static readonly float[] GroupYawDeg = { -8f, 5f, -3f, 9f };
    public const int Groups = 4;

    /// <summary>The velocity of group <paramref name="g"/> for the layer's <paramref name="v"/>.</summary>
    public static Vector3 GroupVelocity(Vector3 v, int g)
    {
        g = Math.Clamp(g, 0, Groups - 1);
        float a = GroupYawDeg[g] * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector3(v.X * c - v.Z * s, v.Y, v.X * s + v.Z * c) * GroupSpeed[g];
    }

    /// <summary>
    /// The soft flakes' near-density share (repair): the old emitter put a drop's 3 s life into a 24 m box, so at wind speeds over 8 m/s only part of a life's flakes were round the player at once; the field shows every drop, so it
    /// draws this share of them to keep the old density: min(1, box / (speed x life)).
    /// </summary>
    public static float EmitterShare(float boxM, float speed, float lifeSeconds) => speed * lifeSeconds > boxM && speed > 0 ? boxM / (speed * lifeSeconds) : 1f;

    static float Mod(double v, float size) => size > 0 ? (float)(v - Math.Floor(v / size) * size) : 0f;

    static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
