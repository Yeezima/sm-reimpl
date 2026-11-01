using System.Numerics;

namespace SmNet.Extras.Gore;

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
/// The shared numbers of the dark gore (Musou Mode and, with <see cref="SmSim.ContentExtras.Gore"/>, classic PvP in Extended): pool sizes, budgets, distances, timings.
/// Every number is ours (kind: ours). Owner, 2026-11-01: "turn up the gore 200%": three times the splatter, droplets and ground stains, much bigger clouds.
/// </summary>
public static class GoreRules
{
    /// <summary>The gore's amount against the first pass (the owner's "200 % more": three times).</summary>
    public const float Amount = 3f;
    /// <summary>Particle pools: gibs (chunks and limbs together), flying droplets, big ground pools, small stains, blood cloud puffs, slashes. A full pool recycles its oldest, never skips.</summary>
    public const int GibPool = 768, DropletPool = 2304, PoolRing = 192, SpatterRing = 576, MistPool = 256, SlashPool = 128;
    /// <summary>Deaths turned into bursts in one frame; the rest wait in the queue, which holds at most <see cref="QueueMax"/> (the oldest go first).</summary>
    public const int DeathsPerFrame = 12, QueueMax = 512;
    /// <summary>Slashes and hit clouds made in one frame (a cleave hits up to 20, an orbital strike many more: none is skipped inside these).</summary>
    public const int SlashesPerFrame = 64, HitsPerFrame = 160;
    /// <summary>Distance bands (m) and the farthest a slash or hit cloud is drawn.</summary>
    public const float FullMetres = 15f, HalfMetres = 35f, SprayMetres = 60f, SlashMetres = 60f, HitMetres = 80f;
    /// <summary>The design's cost: mean milliseconds a frame (scoped timer).</summary>
    public const double BudgetMilliseconds = 0.8;
    /// <summary>Gravity (m/s squared) and the seconds a pool, stain, resting gib and slash last.</summary>
    public const float Gravity = 9.8f, PoolSeconds = 90f, SpatterSeconds = 60f, GibRestSeconds = 30f, SlashSeconds = 0.26f, FadeSeconds = 6f;

    public static GoreLod LodOf(float metres) => metres <= FullMetres ? GoreLod.Full : metres <= HalfMetres ? GoreLod.Half : metres <= SprayMetres ? GoreLod.Spray : GoreLod.None;

    /// <summary>A repeatable roll in [0, 1) from an id and a tick (no random state: the same event rolls the same on every client).</summary>
    public static float Roll(ushort id, uint tick)
    {
        uint h = (id * 2654435761u) ^ (tick * 40503u + 0x9E3779B9u);
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    public static int Count(float v) => v <= 0f ? 0 : Math.Max(1, (int)MathF.Round(v));

    /// <summary>
    /// The burst of one death (<paramref name="style"/> 0 a blow, 1 a strafe, 2 a shot; <paramref name="weight"/> 0.5 a Gretchin, 1 a Boy, 1.6 a Marine, 4 the Warboss) at
    /// <paramref name="metres"/> from the camera. <paramref name="load"/> (0.25 to 1) thins the pieces while the pools fill; the clouds are never thinned.
    /// </summary>
    public static GoreBurst Burst(GoreClass cls, int style, float weight, float metres, float load, bool gibbed)
    {
        var lod = LodOf(metres);
        if (lod == GoreLod.None) return new GoreBurst(cls, lod, 0, 0, 0, 0, 0, 0f, gibbed);
        float size = MathF.Pow(weight, 0.75f);
        int gibs, limbs, droplets, pools, mist;
        float power;
        if (cls == GoreClass.Explosion) { gibs = 14; limbs = 5; droplets = 80; pools = 3; mist = 14; power = 1.8f; }
        else switch (style)
        {
            case 0: gibs = 4; limbs = 2; droplets = 44; pools = 2; mist = 3; power = 1f; break;   // melee and charges weigh heaviest
            case 1: gibs = 6; limbs = 3; droplets = 44; pools = 2; mist = 4; power = 1.2f; break;
            default: gibs = 2; limbs = 0; droplets = 16; pools = 1; mist = 2; power = 0.7f; break;
        }
        // The owner's "up 200 %": three times the pieces (Amount). The clouds are never thinned by the load or the distance band beyond one puff: they always appear.
        float scale = size * Math.Clamp(load, 0.25f, 1f) * (lod == GoreLod.Half ? 0.5f : 1f) * Amount;
        mist = Count(mist * size * (lod == GoreLod.Half ? 0.6f : 1f));
        gibs = Count(gibs * scale); limbs = Count(limbs * scale); droplets = Count(droplets * scale);
        if (lod == GoreLod.Spray)
        {
            // One spray and, for an explosion, one puff: nothing that lies about on the ground.
            gibs = limbs = pools = 0;
            droplets = Math.Min(droplets, 40);
            mist = cls == GoreClass.Explosion ? 2 : 1;
        }
        else if (lod == GoreLod.Half) pools = cls == GoreClass.Explosion ? 1 : pools;
        return new GoreBurst(cls, lod, gibs, limbs, droplets, pools, mist, power * MathF.Pow(weight, 0.25f), gibbed);
    }

}

/// <summary>A queue of work for the frame's budget: at most <see cref="GoreRules.QueueMax"/> items kept (the oldest dropped first), at most <see cref="GoreRules.DeathsPerFrame"/> taken a frame.</summary>
public sealed class GoreQueue<T>
{
    readonly Queue<T> _items = new();
    public int Count => _items.Count;
    /// <summary>Items dropped because the queue was full.</summary>
    public int Dropped { get; private set; }

    public void Add(T item)
    {
        if (_items.Count >= GoreRules.QueueMax) { _items.Dequeue(); Dropped++; }
        _items.Enqueue(item);
    }

    /// <summary>Takes up to <paramref name="max"/> items (oldest first) into <paramref name="into"/>; the number taken.</summary>
    public int Take(List<T> into, int max = GoreRules.DeathsPerFrame)
    {
        int n = 0;
        while (n < max && _items.Count > 0) { into.Add(_items.Dequeue()); n++; }
        return n;
    }

    public void Clear() => _items.Clear();
}

/// <summary>Who runs the dark gore in a match and what damage looks like to it (classic PvP, Extended only).</summary>
public static class PvpGoreRules
{
    /// <summary>
    /// Whether a match's flags run the dark gore on classic PvP: <see cref="SmSim.ContentExtras.Gore"/> set and Musou Mode not (Musou has its own feed). A Faithful match
    /// (no flags), or one with other added content alone, runs none: the client creates no gore view at all.
    /// </summary>
    public static bool Enabled(SmSim.ContentExtras extras) => extras.HasFlag(SmSim.ContentExtras.Gore) && !extras.HasFlag(SmSim.ContentExtras.Musou);

    /// <summary>The look of a health loss between two snapshots (a hit): a shot under 20, a blow or burst of fire under 60, a blast beyond.</summary>
    public static GoreField.HitKind HitKindOf(float healthLost) => healthLost < 20f ? GoreField.HitKind.Shot : healthLost < 60f ? GoreField.HitKind.Melee : GoreField.HitKind.Blast;

    /// <summary>The burst of a player's death at <paramref name="metres"/> from the camera: a blast bursts, a blow is heavy, a shot lighter; a fall out of the level shows none. A body stays (the ragdoll): never gibbed.</summary>
    public static GoreBurst Death(SmSim.KillCause cause, bool splash, float metres, float load)
    {
        if (cause == SmSim.KillCause.World) return GoreBurst.Nothing;
        bool blast = splash || cause is SmSim.KillCause.Thrown or SmSim.KillCause.JumpPack or SmSim.KillCause.Perk;
        return GoreRules.Burst(blast ? GoreClass.Explosion : GoreClass.Gore, cause == SmSim.KillCause.Melee ? 0 : 2, 1.6f, metres, load, false);
    }
}
