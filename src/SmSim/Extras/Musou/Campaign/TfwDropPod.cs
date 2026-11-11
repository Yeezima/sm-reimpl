using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The drop pod assault's numbers (Musou Mode's second support ability; docs/drop-pod-assault.md; owner, 2026-11-12). The owner's are the 30 s of the fight, "a few seconds later" and
/// the four Tactical Marines; every other value is an estimate of ours (spec "Added content: Musou: drop pod assault", the refine register). A change here raises <see cref="TfwRules.Version"/>.
/// </summary>
public static class TfwDropPodRules
{
    /// <summary>Marines in a squad (the owner: "4 tactical marines").</summary>
    public const int Marines = 4;
    /// <summary>Seconds from the call to the touchdown (the owner: "a few seconds later"; ours).</summary>
    public const float LandSeconds = 3f;
    /// <summary>The last part of that the client draws the pod falling in, and how high it starts (m): about 60 m/s, "rapidly" (ours).</summary>
    public const float FallSeconds = 1.5f, FallHeight = 90f;
    /// <summary>The doors open this long after touchdown, and the Marines step out when they have (s; ours).</summary>
    public const float DoorSeconds = 0.6f, StepOutSeconds = 1f;
    /// <summary>The Marines fight this long (s, the owner's 30) from stepping out, then retreat.</summary>
    public const float FightSeconds = 30f;
    /// <summary>A retreating Marine is gone within this of the HQ, or after this long (s) wherever he is (ours).</summary>
    public const float HqRadius = 6f, RetreatMaxSeconds = 40f;
    /// <summary>A killed Marine is taken out of the world this long after (s; ours).</summary>
    public const float FallenSeconds = 3f;
    /// <summary>The pod stays on the ground this long after the last Marine has gone (s; ours).</summary>
    public const float WreckSeconds = 2f;
    /// <summary>The touchdown (owner 2026-11-12: "kill Orks in a 30m radius 3x the damage of a single artillery strike"): every enemy within this (m) takes <see cref="CrushDamageFactor"/> times what one shell deals it, the same shape as a shell's (flat damage inside the sphere, no fall-off: a shell kills every ordinary enemy outright, so the pod does; a boss takes 3 x <see cref="TfwFireRules.BossDamage"/> = 90); it never hurts a player, an AI Marine or a Guardsman.</summary>
    public const float CrushRadius = 30f;
    /// <summary>The factor on a shell's per-target damage (health for an ordinary enemy, <see cref="TfwFireRules.BossDamage"/> for a boss, then the caller's ranged scale), the owner's "3x".</summary>
    public const float CrushDamageFactor = 3f;
    /// <summary>A call at a point nearer than this to the caller is refused (m; ours).</summary>
    public const float MinCallerDistance = 10f;
    /// <summary>The Marines come out round the pod at this radius (m; ours: the door ring of the hull's 3.6 m radius).</summary>
    public const float DoorRing = 5f;
    /// <summary>The pod Marines' "For the Emperor" (owner 2026-11-12: "slightly staggered ... not in unison"): each Marine starts his cry this long after the one before (s; ours, a gap drawn in this range per Marine), the first this long after stepping out.</summary>
    public const float ShoutGapMin = 0.15f, ShoutGapMax = 0.35f;

    /// <summary>
    /// Seconds after the Marines step out that Marine <paramref name="index"/> (0 to <see cref="Marines"/> - 1) begins his cry: the sum of the gaps up to him, each a hash of (<paramref name="seed"/>, number) between
    /// <see cref="ShoutGapMin"/> and <see cref="ShoutGapMax"/>, so every client agrees, the order is the index and no two start together.
    /// </summary>
    public static float ShoutDelay(uint seed, int index)
    {
        float at = 0f;
        for (int k = 0; k <= index; k++)
        {
            uint h = (seed + 1u) * 2654435761u ^ (uint)(k + 1) * 2246822519u;
            h ^= h >> 15; h *= 2654435761u; h ^= h >> 13;
            at += ShoutGapMin + (h & 0xffffu) / 65535f * (ShoutGapMax - ShoutGapMin);
        }
        return at;
    }
    /// <summary>The Marines' scoreboard names (placeholders, labelled so).</summary>
    public static readonly string[] Names = ["Pod Brother Aurelio", "Pod Brother Cato", "Pod Brother Decimus", "Pod Brother Eusebio"];
}

/// <summary>Where a pod is: falling, landed with its doors opening, its Marines fighting, retreating, or the wreck waiting to go.</summary>
public enum TfwPodPhase : byte { Falling, Doors, Fighting, Retreating, Wreck }

/// <summary>One pod: its caller, the touchdown tick and place, the tick its Marines step out and the tick their fight ends, the phase, and the Marines it brought (puppet ids).</summary>
public sealed class TfwPod(int caller, uint landTick, uint spawnTick, uint fightEndTick, Vector3 at)
{
    public int Caller { get; } = caller;
    public uint LandTick { get; } = landTick;
    public uint SpawnTick { get; } = spawnTick;
    public uint FightEndTick { get; } = fightEndTick;
    public Vector3 At { get; } = at;
    public TfwPodPhase Phase { get; internal set; } = TfwPodPhase.Falling;
    /// <summary>The first tick of the retreat (set when it begins), and of the wreck's removal.</summary>
    public uint RetreatTick { get; internal set; }
    public uint EndTick { get; internal set; }
    internal readonly List<int> MarineIds = new();
}

/// <summary>The pods of a run (one per player at a time).</summary>
public sealed partial class TfwRun
{
    readonly List<TfwPod> _pods = new();

    /// <summary>The pods in the air or on the ground.</summary>
    public IReadOnlyList<TfwPod> Pods => _pods;

    /// <summary>The pod <paramref name="player"/> has out, if any.</summary>
    public TfwPod? PodOf(int player)
    {
        foreach (var p in _pods) if (p.Caller == player) return p;
        return null;
    }

    /// <summary>Puppet ids a pod has called but not yet used: its Marines step out after the fall and the doors (<see cref="TfwDropPodRules.Marines"/> each).</summary>
    public int PodIdsReserved => _pods.Count(p => p.Phase is TfwPodPhase.Falling or TfwPodPhase.Doors) * TfwDropPodRules.Marines;

    internal void AddPod(TfwPod pod) => _pods.Add(pod);
    internal void RemovePod(TfwPod pod) => _pods.Remove(pod);
    /// <summary>Test aid: the pod is gone (its cooldown is the fire's, unchanged).</summary>
    public void RemovePodForTests(TfwPod pod) => _pods.Remove(pod);
    internal void ForgetPods() => _pods.Clear();
}
