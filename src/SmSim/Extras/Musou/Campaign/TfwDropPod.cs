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
    /// <summary>The touchdown kills every ordinary enemy within this (m; ours: the owner did not say), a boss takes <see cref="TfwFireRules.BossDamage"/>; it never hurts a player, an AI Marine or a Guardsman.</summary>
    public const float CrushRadius = 6f;
    /// <summary>A call at a point nearer than this to the caller is refused (m; ours).</summary>
    public const float MinCallerDistance = 10f;
    /// <summary>The Marines come out round the pod at this radius (m; ours: the door ring of the hull's 3.6 m radius).</summary>
    public const float DoorRing = 5f;
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

    internal void AddPod(TfwPod pod) => _pods.Add(pod);
    internal void RemovePod(TfwPod pod) => _pods.Remove(pod);
    internal void ForgetPods() => _pods.Clear();
}
