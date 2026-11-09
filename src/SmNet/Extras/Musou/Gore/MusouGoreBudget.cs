namespace SmNet.Extras.Musou;

/// <summary>Whose attack a hit or a kill is, as the gore budget ranks it.</summary>
public enum MusouGoreSource : byte
{
    /// <summary>This client's own player.</summary>
    Own = 0,
    /// <summary>Another Marine: another human player or an AI Marine.</summary>
    Ally = 1,
    /// <summary>Everyone else: fire support (player 0), and anything without an attacker.</summary>
    Other = 2,
}

/// <summary>
/// Who gets gore first in heavy fighting (musou-net-budget, the owner 2026-11-09: "ensure that all player attacks in particular - then allied space marine attacks have high
/// priority for generating gore effects during heavy fighting"; added content, all values ours). The client's own hits and kills first, the allied Marines' second, the rest last,
/// at every client stage: the hit effects' rate, the kills' blood a frame, and the effect player's priority (when its 160 effects are full the lowest priority goes first, and the
/// crowd's tracers and muzzle flashes are lowest of all, <see cref="CrowdFirePriority"/>). <see cref="Old"/> restores the single shared budget of before (a test aid).
/// </summary>
public static class MusouGorePriority
{
    /// <summary>The source of an attack by <paramref name="attacker"/> as seen by the client whose player is <paramref name="own"/>.</summary>
    public static MusouGoreSource SourceOf(int attacker, int own) =>
        attacker <= 0 ? MusouGoreSource.Other : attacker == own ? MusouGoreSource.Own : MusouGoreSource.Ally;

    /// <summary>Hit effects a second and the most saved, by source (the shared bucket before: 16 a second, 8 saved, for everyone).</summary>
    public static (float PerSecond, float Burst) HitRate(MusouGoreSource s) => s switch
    {
        MusouGoreSource.Own => (40f, 16f),
        MusouGoreSource.Ally => (20f, 8f),
        _ => (10f, 4f),
    };

    /// <summary>
    /// Metres from the camera within which a hit plays its blood impact, by source (30 for all before: 44 % of a fighting player's own hits fell beyond it, as the camera sits behind
    /// the player and the guns reach 35 m and more).
    /// </summary>
    public static float HitMetres(MusouGoreSource s) => s switch { MusouGoreSource.Own => 60f, MusouGoreSource.Ally => 40f, _ => 30f };

    /// <summary>Kills a frame that play their blood, by source (8 shared before; the rest play none).</summary>
    public static int KillsPerFrame(MusouGoreSource s) => s switch { MusouGoreSource.Own => 16, MusouGoreSource.Ally => 8, _ => 4 };

    /// <summary>The effect player's priority of a blood or hit effect by source: the own above the allies above the rest (the effect player drops the oldest of the lowest first).</summary>
    public static int FxPriority(MusouGoreSource s) => s switch { MusouGoreSource.Own => 2, MusouGoreSource.Ally => 1, _ => 0 };

    /// <summary>The priority of the crowd's fire (Ork and Guardsmen tracers and muzzle flashes): the first to go when the effects are full (1 for the tracers before).</summary>
    public const int CrowdFirePriority = 0;
}

/// <summary>
/// The client's gore budget, frame by frame (engine-free, so the ranks are tested): <see cref="Refill"/> once a frame, then <see cref="TakeHit"/> for each hit effect and
/// <see cref="TakeKill"/> for each kill's blood. Each source has its own bucket and its own frame cap, so the crowd's and the allies' gore never takes the own hits' room.
/// With <see cref="Old"/> every source shares one bucket of 16 a second (8 saved) and one cap of 8 kills a frame, as before.
/// </summary>
public sealed class MusouGoreBudget
{
    readonly float[] _tokens = new float[3];
    readonly int[] _kills = new int[3];
    int _sharedKills;
    float _shared = OldBurst;

    const float OldPerSecond = 16f, OldBurst = 8f;
    const int OldKillsPerFrame = 8;

    /// <summary>The budget before musou-net-budget (one bucket, one cap for every source).</summary>
    public bool Old { get; init; }

    /// <summary>Counts by source since the start: hits given an effect and refused by the rate; kills given blood and refused by the frame cap.</summary>
    public readonly long[] HitsTaken = new long[3], HitsRefused = new long[3], KillsTaken = new long[3], KillsRefused = new long[3];

    public MusouGoreBudget()
    {
        for (int s = 0; s < 3; s++) _tokens[s] = MusouGorePriority.HitRate((MusouGoreSource)s).Burst;
    }

    /// <summary>A new frame of <paramref name="seconds"/>: the buckets refill and the frame caps start again.</summary>
    public void Refill(float seconds)
    {
        if (!(seconds >= 0f)) seconds = 0f;
        for (int s = 0; s < 3; s++)
        {
            var (rate, burst) = MusouGorePriority.HitRate((MusouGoreSource)s);
            _tokens[s] = MathF.Min(burst, _tokens[s] + rate * seconds);
            _kills[s] = 0;
        }
        _shared = MathF.Min(OldBurst, _shared + OldPerSecond * seconds);
        _sharedKills = 0;
    }

    /// <summary>Whether a hit by <paramref name="source"/> may play its effect now (and takes its token).</summary>
    public bool TakeHit(MusouGoreSource source)
    {
        int s = (int)source;
        ref float tokens = ref Old ? ref _shared : ref _tokens[s];
        if (tokens < 1f) { HitsRefused[s]++; return false; }
        tokens -= 1f;
        HitsTaken[s]++;
        return true;
    }

    /// <summary>Whether a kill by <paramref name="source"/> may play its blood this frame (and counts it).</summary>
    public bool TakeKill(MusouGoreSource source)
    {
        int s = (int)source;
        if (Old ? _sharedKills >= OldKillsPerFrame : _kills[s] >= MusouGorePriority.KillsPerFrame(source)) { KillsRefused[s]++; return false; }
        if (Old) _sharedKills++; else _kills[s]++;
        KillsTaken[s]++;
        return true;
    }
}
