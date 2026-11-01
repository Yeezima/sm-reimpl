using System.Numerics;

namespace SmNet.Extras.Musou;

/// <summary>A chunk or a limb flying, bouncing and then lying about (<see cref="Kind"/> 0 chunk, 1 limb). Game space, Z up.</summary>
public struct GoreGib
{
    public bool Live, Resting;
    public byte Kind, Bounces;
    public Vector3 Pos, Vel, Angle, Spin, Colour;
    public float Size, Age, Floor, Drip;
}

/// <summary>A droplet of the spray: a streak along its velocity until it lands.</summary>
public struct GoreDrop
{
    public bool Live;
    public Vector3 Pos, Vel, Colour;
    public float Size, Age, Life, Floor;
}

/// <summary>A pool on the ground: it spreads to <see cref="Radius"/> and stays.</summary>
public struct GorePool
{
    public bool Live;
    public Vector3 Pos, Colour;
    public float Radius, Age, Seed;
}

/// <summary>A small spot on the ground.</summary>
public struct GoreSpatter
{
    public bool Live;
    public Vector3 Pos, Colour;
    public float Size, Rotation, Age, Seed;
}

/// <summary>A red puff of a bloody explosion (a camera-facing cloud that swells and fades).</summary>
public struct GoreMist
{
    public bool Live;
    public Vector3 Pos, Vel;
    public float Size, Age, Life;
}

/// <summary>A cleave's slash across one enemy's body: <see cref="Axis"/> the stroke, <see cref="Up"/> across it, both in the plane facing the camera.</summary>
public struct GoreSlash
{
    public bool Live;
    public Vector3 Pos, Axis, Up, Normal;
    public float Length, Age, Seed;
}

/// <summary>
/// The simulation of Musou Mode's gore, engine-free (M10): fixed pools of gibs (256), droplets, ground pools (a ring of 64), spatter, mist and cleave slashes, stepped
/// ballistically with a cheap bounce on the floor under the enemy (no physics). Nothing is allocated after construction; a full pool recycles its oldest particle
/// (counted). The Godot view (<c>client/Extras/Musou/Gore/MusouGoreView.cs</c>) only draws these arrays. Game space (metres, Z up). Every number is ours.
/// </summary>
public sealed class MusouGoreField
{
    public readonly GoreGib[] Gibs = new GoreGib[MusouGoreRules.GibPool];
    public readonly GoreDrop[] Drops = new GoreDrop[MusouGoreRules.DropletPool];
    public readonly GorePool[] Pools = new GorePool[MusouGoreRules.PoolRing];
    public readonly GoreSpatter[] Spatters = new GoreSpatter[MusouGoreRules.SpatterRing];
    public readonly GoreMist[] Mists = new GoreMist[MusouGoreRules.MistPool];
    public readonly GoreSlash[] Slashes = new GoreSlash[MusouGoreRules.SlashPool];

    readonly Random _rng;
    int _gibNext, _dropNext, _poolNext, _spatterNext, _mistNext, _slashNext;

    /// <summary>Live particles of each kind after the last <see cref="Step"/> (or spawn).</summary>
    public int LiveGibs { get; private set; }
    public int LiveDrops { get; private set; }
    public int LivePools { get; private set; }
    public int LiveSpatters { get; private set; }
    public int LiveMists { get; private set; }
    public int LiveSlashes { get; private set; }
    /// <summary>Particles spawned in all, and live ones overwritten by a newer one because their pool was full.</summary>
    public long Spawned { get; private set; }
    public long Recycled { get; private set; }
    /// <summary>Seconds simulated.</summary>
    public double Time { get; private set; }
    /// <summary>Raised whenever a pool or spot is made or ends: the ground changed (the view rebuilds it then, and otherwise only a few times a second for the spreading and fading).</summary>
    public int GroundVersion { get; private set; }

    public MusouGoreField(int seed = 40_000) => _rng = new Random(seed);

    float F() => _rng.NextSingle();
    float Range(float a, float b) => a + (b - a) * F();

    Vector3 Unit()
    {
        float z = Range(-1f, 1f), a = Range(0f, MathF.Tau), r = MathF.Sqrt(1f - z * z);
        return new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
    }

    static Vector3 Norm(Vector3 v, Vector3 fallback) { float l = v.Length(); return l > 1e-5f ? v / l : fallback; }

    /// <summary>How much of a burst to make while the pools fill (0.25 to 1; <see cref="MusouGoreRules.Plan"/>'s load).</summary>
    public float LoadScale()
    {
        float load = MathF.Max(LiveGibs / (float)MusouGoreRules.GibPool, LiveDrops / (float)MusouGoreRules.DropletPool);
        return Math.Clamp(1.3f - load, 0.25f, 1f);
    }

    Vector3 Blood()
    {
        float shade = F();
        return new Vector3(Range(0.3f, 0.75f) * (0.6f + 0.4f * shade), Range(0.004f, 0.02f), Range(0.008f, 0.04f));
    }

    Vector3 Flesh(bool ork)
    {
        float r = F();
        if (ork && r < 0.14f) return new Vector3(Range(0.07f, 0.13f), Range(0.14f, 0.24f), Range(0.04f, 0.08f));   // green hide
        if (r < 0.88f) return new Vector3(Range(0.2f, 0.38f), Range(0.015f, 0.04f), Range(0.015f, 0.04f));   // dark meat
        return new Vector3(Range(0.45f, 0.58f), Range(0.14f, 0.22f), Range(0.13f, 0.2f));   // pale flesh and fat
    }

    /// <summary>
    /// Makes one death's burst at <paramref name="at"/> (the body's centre): <paramref name="push"/> the death event's direction (0: none), <paramref name="floor"/> the
    /// ground under the enemy, <paramref name="ork"/> whether chunks may be green hide.
    /// </summary>
    public void Spawn(in GoreBurst b, Vector3 at, Vector3 push, float floor, bool ork)
    {
        if (b.Class == GoreClass.None || b.Lod == GoreLod.None) return;
        bool boom = b.Class == GoreClass.Explosion;
        var pd = Norm(new Vector3(push.X, push.Y, 0f), Vector3.Zero);
        float power = b.Power;
        for (int i = 0; i < b.Gibs + b.Limbs; i++)
        {
            bool limb = i >= b.Gibs;
            var dir = boom ? Norm(new Vector3(Unit().X, Unit().Y, MathF.Abs(F()) + 0.2f), Vector3.UnitZ) : Norm(pd * 0.8f + Unit() * 0.9f, Vector3.UnitZ);
            float speed = Range(2.5f, 7.5f) * power;
            ref var g = ref NextGib();
            g = new GoreGib
            {
                Live = true, Kind = (byte)(limb ? 1 : 0), Pos = at + Unit() * 0.2f, Floor = floor,
                Vel = new Vector3(dir.X, dir.Y, 0f) * speed + new Vector3(0f, 0f, Range(2f, 6.5f) * MathF.Sqrt(power) * (boom ? 1.4f : 1f)),
                Angle = new Vector3(Range(0f, MathF.Tau), Range(0f, MathF.Tau), Range(0f, MathF.Tau)),
                Spin = new Vector3(Range(-12f, 12f), Range(-12f, 12f), Range(-12f, 12f)),
                Size = limb ? Range(0.13f, 0.22f) * (0.8f + 0.3f * power) : Range(0.06f, 0.17f) * (0.8f + 0.3f * power),
                Colour = limb ? (F() < 0.18f ? new Vector3(0.6f, 0.56f, 0.48f) : Flesh(ork)) : Flesh(ork),
                Drip = Range(0f, 0.08f),
            };
        }
        for (int i = 0; i < b.Droplets; i++)
        {
            var dir = boom ? Norm(new Vector3(Unit().X, Unit().Y, MathF.Abs(F()) * 0.9f + 0.1f), Vector3.UnitZ) : Norm(pd * 1.2f + Unit() * 0.9f, Vector3.UnitZ);
            float speed = Range(3f, boom ? 14f : 10f) * power;
            ref var d = ref NextDrop();
            d = new GoreDrop
            {
                Live = true, Pos = at + Unit() * 0.15f, Floor = floor, Colour = Blood(), Size = Range(0.02f, 0.055f) * (0.8f + 0.25f * power), Life = Range(1.2f, 2.4f),
                Vel = new Vector3(dir.X * speed, dir.Y * speed, dir.Z * speed + Range(1f, 3.5f)),
            };
        }
        for (int i = 0; i < b.Pools; i++)
        {
            ref var p = ref NextPool();
            float radius = boom ? Range(1.2f, 1.9f) * (i == 0 ? 1f : 0.55f) : Range(0.38f, 0.65f) * MathF.Sqrt(power);
            var off = i == 0 ? Vector3.Zero : new Vector3(Unit().X, Unit().Y, 0f) * 0.9f;
            GroundVersion++;
            p = new GorePool { Live = true, Pos = new Vector3(at.X + off.X, at.Y + off.Y, floor), Colour = new Vector3(Range(0.42f, 0.6f), 0.008f, 0.02f), Radius = radius, Seed = F() };
        }
        for (int i = 0; i < b.Mist; i++)
        {
            ref var m = ref NextMist();
            m = new GoreMist { Live = true, Pos = at + Unit() * 0.5f, Vel = Unit() * 1.2f + new Vector3(0f, 0f, 0.7f), Size = Range(1.5f, 2.6f), Life = Range(0.8f, 1.3f) };
        }
        Recount();
    }

    /// <summary>Item 22: the slash across an enemy a cleave hit at <paramref name="at"/> (its chest), struck from <paramref name="from"/>, seen from <paramref name="camera"/>, with a spray of drops along the stroke.</summary>
    public void Cleave(Vector3 at, Vector3 from, Vector3 camera)
    {
        var view = Norm(camera - at, new Vector3(0f, -1f, 0f));
        var right = Norm(Vector3.Cross(Vector3.UnitZ, view), Vector3.UnitX);
        var up = Vector3.Cross(view, right);
        // A diagonal stroke from the side the blow came from: 25 to 60 degrees off the horizontal, falling toward the far side.
        float side = Vector3.Dot(at - from, right) >= 0f ? 1f : -1f;
        float angle = Range(0.45f, 1.05f) * (F() < 0.5f ? 1f : -1f);
        var axis = Norm(right * MathF.Cos(angle) * side + up * MathF.Sin(angle), right);
        ref var s = ref NextSlash();
        s = new GoreSlash { Live = true, Pos = at + view * 0.45f, Axis = axis, Up = Norm(Vector3.Cross(view, axis), up), Normal = view, Length = Range(1.3f, 1.9f), Seed = F() };
        int n = (int)MathF.Round(9 * LoadScale());
        for (int i = 0; i < n; i++)
        {
            ref var d = ref NextDrop();
            d = new GoreDrop
            {
                Live = true, Pos = at + axis * Range(-0.7f, 0.7f), Floor = at.Z - 1.1f, Colour = Blood(), Size = Range(0.02f, 0.05f), Life = Range(0.8f, 1.8f),
                Vel = axis * Range(2.5f, 8f) + Unit() * 1.4f + new Vector3(0f, 0f, Range(0.5f, 2.5f)),
            };
        }
        Recount();
    }

    /// <summary>A small spray at a shot's hit (<paramref name="dir"/> the way the shot went).</summary>
    public void Spray(Vector3 at, Vector3 dir, int count, float power)
    {
        var d0 = Norm(dir, Vector3.UnitY);
        for (int i = 0; i < count; i++)
        {
            var v = Norm(d0 * 1.1f + Unit() * 0.8f, d0);
            ref var d = ref NextDrop();
            d = new GoreDrop
            {
                Live = true, Pos = at, Floor = at.Z - 1.1f, Colour = Blood(), Size = Range(0.015f, 0.04f), Life = Range(0.6f, 1.4f),
                Vel = v * Range(2f, 7f) * power + new Vector3(0f, 0f, Range(0.5f, 2f)),
            };
        }
        Recount();
    }

    ref GoreGib NextGib() { ref var g = ref Gibs[_gibNext]; if (g.Live) Recycled++; Spawned++; _gibNext = (_gibNext + 1) % Gibs.Length; return ref g; }
    ref GoreDrop NextDrop() { ref var g = ref Drops[_dropNext]; if (g.Live) Recycled++; Spawned++; _dropNext = (_dropNext + 1) % Drops.Length; return ref g; }
    ref GorePool NextPool() { ref var g = ref Pools[_poolNext]; if (g.Live) Recycled++; Spawned++; _poolNext = (_poolNext + 1) % Pools.Length; return ref g; }
    ref GoreSpatter NextSpatter() { ref var g = ref Spatters[_spatterNext]; if (g.Live) Recycled++; Spawned++; _spatterNext = (_spatterNext + 1) % Spatters.Length; return ref g; }
    ref GoreMist NextMist() { ref var g = ref Mists[_mistNext]; if (g.Live) Recycled++; Spawned++; _mistNext = (_mistNext + 1) % Mists.Length; return ref g; }
    ref GoreSlash NextSlash() { ref var g = ref Slashes[_slashNext]; if (g.Live) Recycled++; Spawned++; _slashNext = (_slashNext + 1) % Slashes.Length; return ref g; }

    void Splat(Vector3 at, float floor, float size, Vector3 colour)
    {
        ref var s = ref NextSpatter();
        GroundVersion++;
        s = new GoreSpatter { Live = true, Pos = new Vector3(at.X, at.Y, floor), Size = size, Rotation = Range(0f, MathF.Tau), Colour = colour * 0.9f, Seed = F() };
    }

    /// <summary>Advances every particle by <paramref name="seconds"/> (at most 0.1).</summary>
    public void Step(float seconds)
    {
        float dt = Math.Clamp(seconds, 0f, 0.1f);
        Time += dt;
        float dropLoad = LiveDrops / (float)MusouGoreRules.DropletPool;
        for (int i = 0; i < Gibs.Length; i++)
        {
            ref var g = ref Gibs[i];
            if (!g.Live) continue;
            g.Age += dt;
            if (g.Resting)
            {
                if (g.Age > MusouGoreRules.GibRestSeconds + MusouGoreRules.FadeSeconds) g.Live = false;
                continue;
            }
            g.Vel.Z -= MusouGoreRules.Gravity * dt;
            g.Pos += g.Vel * dt;
            g.Angle += g.Spin * dt;
            g.Drip -= dt;
            if (g.Drip <= 0f && dropLoad < 0.7f)
            {
                // A trail: a drop falls off the flying piece now and then.
                g.Drip = Range(0.05f, 0.1f);
                ref var d = ref NextDrop();
                d = new GoreDrop { Live = true, Pos = g.Pos, Floor = g.Floor, Colour = Blood(), Size = Range(0.015f, 0.035f), Life = 1.5f, Vel = Unit() * 0.6f + g.Vel * 0.2f };
            }
            float rest = g.Floor + g.Size * 0.4f;
            if (g.Pos.Z <= rest)
            {
                g.Pos.Z = rest;
                if (g.Vel.Z < -1.3f && g.Bounces < 3)
                {
                    g.Vel = new Vector3(g.Vel.X * 0.55f, g.Vel.Y * 0.55f, -g.Vel.Z * 0.38f);
                    g.Spin *= 0.6f;
                    g.Bounces++;
                    Splat(g.Pos, g.Floor, g.Size * Range(1.6f, 3.2f), Blood());
                }
                else
                {
                    g.Resting = true; g.Vel = Vector3.Zero; g.Spin = Vector3.Zero; g.Age = 0f;
                    Splat(g.Pos, g.Floor, g.Size * Range(2.2f, 3.6f), Blood());
                }
            }
        }
        for (int i = 0; i < Drops.Length; i++)
        {
            ref var d = ref Drops[i];
            if (!d.Live) continue;
            d.Age += dt;
            d.Vel.Z -= MusouGoreRules.Gravity * dt;
            d.Pos += d.Vel * dt;
            if (d.Pos.Z <= d.Floor)
            {
                if (F() < 0.45f) Splat(d.Pos, d.Floor, d.Size * Range(3f, 7f), d.Colour);
                d.Live = false;
            }
            else if (d.Age > d.Life) d.Live = false;
        }
        for (int i = 0; i < Pools.Length; i++) { ref var p = ref Pools[i]; if (p.Live) { p.Age += dt; if (p.Age > MusouGoreRules.PoolSeconds) { p.Live = false; GroundVersion++; } } }
        for (int i = 0; i < Spatters.Length; i++) { ref var s = ref Spatters[i]; if (s.Live) { s.Age += dt; if (s.Age > MusouGoreRules.SpatterSeconds) { s.Live = false; GroundVersion++; } } }
        for (int i = 0; i < Mists.Length; i++)
        {
            ref var m = ref Mists[i];
            if (!m.Live) continue;
            m.Age += dt;
            m.Pos += m.Vel * dt;
            m.Vel *= MathF.Max(0f, 1f - 2f * dt);
            if (m.Age > m.Life) m.Live = false;
        }
        for (int i = 0; i < Slashes.Length; i++) { ref var s = ref Slashes[i]; if (s.Live) { s.Age += dt; if (s.Age > MusouGoreRules.SlashSeconds) s.Live = false; } }
        Recount();
    }

    void Recount()
    {
        int g = 0, d = 0, p = 0, s = 0, m = 0, l = 0;
        foreach (ref readonly var x in Gibs.AsSpan()) if (x.Live) g++;
        foreach (ref readonly var x in Drops.AsSpan()) if (x.Live) d++;
        foreach (ref readonly var x in Pools.AsSpan()) if (x.Live) p++;
        foreach (ref readonly var x in Spatters.AsSpan()) if (x.Live) s++;
        foreach (ref readonly var x in Mists.AsSpan()) if (x.Live) m++;
        foreach (ref readonly var x in Slashes.AsSpan()) if (x.Live) l++;
        LiveGibs = g; LiveDrops = d; LivePools = p; LiveSpatters = s; LiveMists = m; LiveSlashes = l;
    }

    /// <summary>Forgets every particle (a new run).</summary>
    public void Clear()
    {
        Array.Clear(Gibs); Array.Clear(Drops); Array.Clear(Pools); Array.Clear(Spatters); Array.Clear(Mists); Array.Clear(Slashes);
        Recount();
    }
}
