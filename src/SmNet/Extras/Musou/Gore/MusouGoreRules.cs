using System.Numerics;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>What a death makes on the client (Musou pass 2 M10, owner items 8 and 23).</summary>
public enum GoreClass : byte
{
    /// <summary>Nothing (a failsafe removal, or a death no Marine's attack caused).</summary>
    None = 0,
    /// <summary>The gore of an attack by a player, a retainer or a Marine: gibs, sprays, spatter and a pool.</summary>
    Gore = 1,
    /// <summary>Item 23: an explosion, a frag grenade, artillery or a lascannon shot bursts the enemy in a bloody explosion.</summary>
    Explosion = 2,
}

/// <summary>How much of a burst is made at a distance (design section D: full under 15 m, half to 35 m, one spray to 60 m, nothing beyond).</summary>
public enum GoreLod : byte { Full = 0, Half = 1, Spray = 2, None = 3 }

/// <summary>What one death makes: the counts of each kind of particle, the power (scales speeds and sizes) and whether the body is gibbed (no corpse is drawn).</summary>
public readonly record struct GoreBurst(GoreClass Class, GoreLod Lod, int Gibs, int Limbs, int Droplets, int Pools, int Mist, float Power, bool Gibbed)
{
    public static GoreBurst Nothing => new(GoreClass.None, GoreLod.None, 0, 0, 0, 0, 0, 0f, false);
}

/// <summary>
/// The rules of Musou Mode's gore, engine-free (Musou pass 2 M10; design section D and the owner's items 8, 22 and 23). Every number is ours (kind: ours), inspired by
/// the Project Brutality mod ("Ha Ha Gore Goes Burr"): the budgets are the design's (gib pool 256, 12 deaths processed a frame, the distance bands), the pools of
/// droplets, pools on the ground and spatter are sized for 200 enemies and heavier death rates.
/// </summary>
public static class MusouGoreRules
{
    /// <summary>Particle pools: gibs (chunks and limbs together), flying droplets, big ground pools (design: a ring of 64), small spatter, red mist puffs, cleave slashes.</summary>
    public const int GibPool = 256, DropletPool = 768, PoolRing = 64, SpatterRing = 192, MistPool = 48, SlashPool = 48;
    /// <summary>Deaths turned into bursts in one frame (design: 12); the rest wait in the queue, which holds at most <see cref="QueueMax"/> (the oldest go first).</summary>
    public const int DeathsPerFrame = 12, QueueMax = 96;
    /// <summary>Cleave slashes and hit sprays made in one frame (ours: a swing hits up to 20 enemies).</summary>
    public const int SlashesPerFrame = 24, SpraysPerFrame = 10;
    /// <summary>Distance bands (m; design section D) and the farthest a cleave slash is drawn (ours).</summary>
    public const float FullMetres = 15f, HalfMetres = 35f, SprayMetres = 60f, SlashMetres = 40f;
    /// <summary>The design's cost: mean milliseconds a frame (scoped timer).</summary>
    public const double BudgetMilliseconds = 0.8;
    /// <summary>Gravity (m/s squared) and the seconds a pool, spatter, resting gib and slash last (ours).</summary>
    public const float Gravity = 9.8f, PoolSeconds = 90f, SpatterSeconds = 60f, GibRestSeconds = 30f, SlashSeconds = 0.26f, FadeSeconds = 6f;

    public static GoreLod LodOf(float metres) => metres <= FullMetres ? GoreLod.Full : metres <= HalfMetres ? GoreLod.Half : metres <= SprayMetres ? GoreLod.Spray : GoreLod.None;

    /// <summary>
    /// What a death is. The killer is the death event's player (0: none; a human's id or a retainer's; Guardsmen's kills carry 0). A blast (a frag, a launcher or plasma
    /// burst), artillery (Barrage) and a lascannon shot (<paramref name="killerHasLascannon"/>: the killer's weapon in hand, read by the view) burst whoever the killer; a
    /// Thunderhawk strafe is gore; the rest only when a player or retainer killed. A failsafe removal (World) is nothing.
    /// </summary>
    public static GoreClass Classify(MusouDeathKind cause, int killer, bool killerHasLascannon) => cause switch
    {
        MusouDeathKind.World => GoreClass.None,
        MusouDeathKind.Barrage or MusouDeathKind.Blast => GoreClass.Explosion,
        MusouDeathKind.Strafe => GoreClass.Gore,
        _ => killer <= 0 ? GoreClass.None : killerHasLascannon && cause == MusouDeathKind.Shot ? GoreClass.Explosion : GoreClass.Gore,
    };

    /// <summary>How big an enemy is for gore: a Gretchin half a Boy, the Warboss four.</summary>
    public static float Weight(MusouEnemyType type) => type switch
    {
        MusouEnemyType.Gretchin => 0.5f,
        MusouEnemyType.Boy or MusouEnemyType.ShootaBoy => 1f,
        MusouEnemyType.ArdBoy => 1.5f,
        MusouEnemyType.ChaosMarine => 1.6f,
        MusouEnemyType.Nob => 2.2f,
        MusouEnemyType.Warboss => 4f,
        _ => 1f,
    };

    /// <summary>The chance a gore death (not an explosion, which always bursts) leaves no corpse: melee and charges 35 % (15 % for the big), a strafe 60 %, a shot never.</summary>
    public static float GibbedChance(MusouDeathKind cause, MusouEnemyType type) => cause switch
    {
        MusouDeathKind.Melee => Weight(type) <= 1f ? 0.35f : 0.15f,
        MusouDeathKind.Strafe => 0.6f,
        _ => 0f,
    };

    /// <summary>A repeatable roll in [0, 1) from an enemy id and its death tick (no random state: the same death gibs the same way on every client).</summary>
    public static float Roll(ushort id, uint tick)
    {
        uint h = (id * 2654435761u) ^ (tick * 40503u + 0x9E3779B9u);
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    /// <summary>
    /// The burst of one death at <paramref name="metres"/> from the camera. <paramref name="load"/> (0.25 to 1, <see cref="MusouGoreField.LoadScale"/>) thins every count
    /// while the pools are filling, so heavy death rates (200 enemies, a barrage) recycle fewer live particles.
    /// </summary>
    public static GoreBurst Plan(MusouDeathKind cause, MusouEnemyType type, int killer, bool killerHasLascannon, float metres, float load, ushort id, uint tick)
    {
        var cls = Classify(cause, killer, killerHasLascannon);
        if (cls == GoreClass.None) return GoreBurst.Nothing;
        var lod = LodOf(metres);
        bool gibbed = cls == GoreClass.Explosion || lod != GoreLod.None && Roll(id, tick) < GibbedChance(cause, type);
        if (lod == GoreLod.None) return new GoreBurst(cls, lod, 0, 0, 0, 0, 0, 0f, gibbed);

        float weight = Weight(type);
        float size = MathF.Pow(weight, 0.75f);
        int gibs, limbs, droplets, pools, mist;
        float power;
        if (cls == GoreClass.Explosion) { gibs = 20; limbs = 7; droplets = 80; pools = 2; mist = 3; power = 1.8f; }
        else switch (cause)
        {
            case MusouDeathKind.Melee: gibs = 5; limbs = 2; droplets = 36; pools = 1; mist = 0; power = 1f; break;   // melee and charges weigh heaviest
            case MusouDeathKind.Strafe: gibs = 7; limbs = 3; droplets = 44; pools = 1; mist = 0; power = 1.2f; break;
            default: gibs = 2; limbs = 0; droplets = 16; pools = 1; mist = 0; power = 0.7f; break;
        }
        float scale = size * Math.Clamp(load, 0.25f, 1f) * (lod == GoreLod.Half ? 0.5f : 1f);
        gibs = Count(gibs * scale); limbs = Count(limbs * scale); droplets = Count(droplets * scale); mist = Count(mist * scale * (cls == GoreClass.Explosion ? 1f : 0f));
        if (lod == GoreLod.Spray)
        {
            // One spray and, for an explosion, one puff: nothing that lies about on the ground.
            gibs = limbs = pools = 0;
            droplets = Math.Min(droplets, 14);
            mist = cls == GoreClass.Explosion ? 1 : 0;
        }
        else if (lod == GoreLod.Half) pools = cls == GoreClass.Explosion ? 1 : pools;
        return new GoreBurst(cls, lod, gibs, limbs, droplets, pools, mist, power * MathF.Pow(weight, 0.25f), gibbed);
    }

    static int Count(float v) => v <= 0f ? 0 : Math.Max(1, (int)MathF.Round(v));

    /// <summary>
    /// A cleave slash: whether it is drawn for a hit at <paramref name="metres"/> (item 22: a slashing bloody visual on each enemy a cleave hits). A melee hit is marker 3
    /// on a <see cref="MusouEventKind.Hit"/>; a player or retainer made it (<paramref name="attacker"/> above 0).
    /// </summary>
    public static bool SlashDrawn(int attacker, float metres) => attacker > 0 && metres <= SlashMetres;
}

/// <summary>A queue of work for the frame's budget: at most <see cref="MusouGoreRules.QueueMax"/> items kept (the oldest dropped first), at most <see cref="MusouGoreRules.DeathsPerFrame"/> taken a frame.</summary>
public sealed class GoreQueue<T>
{
    readonly Queue<T> _items = new();
    public int Count => _items.Count;
    /// <summary>Items dropped because the queue was full.</summary>
    public int Dropped { get; private set; }

    public void Add(T item)
    {
        if (_items.Count >= MusouGoreRules.QueueMax) { _items.Dequeue(); Dropped++; }
        _items.Enqueue(item);
    }

    /// <summary>Takes up to <paramref name="max"/> items (oldest first) into <paramref name="into"/>; the number taken.</summary>
    public int Take(List<T> into, int max = MusouGoreRules.DeathsPerFrame)
    {
        int n = 0;
        while (n < max && _items.Count > 0) { into.Add(_items.Dequeue()); n++; }
        return n;
    }

    public void Clear() => _items.Clear();
}
