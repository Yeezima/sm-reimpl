using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront.Hq;

/// <summary>
/// The numbers of Warfront's HQ defense (slice W13b; spec "Warfront: the HQ defense"). The win is the owner's (2026-11-17: "The HQ defense is won by surviving 8 minutes"); every other value is ours, an
/// estimate, tuned after the chokepoint measurement (<c>smtool warfront-hq --measure</c>).
/// </summary>
public static class WarfrontHqBattleRules
{
    /// <summary>The time to survive from the battle's start, seconds (owner). Waves keep coming until it ends.</summary>
    public const float SurviveSeconds = 480f;
    /// <summary>When the first wave leaves the assembly ground, seconds after the landing (so nobody meets Orks at the gate before he can walk there: the chamber is about 220 m behind it).</summary>
    public const float FirstWaveSeconds = 45f;
    /// <summary>The time between two waves leaving, seconds (0:45, 3:15, 5:45 in the plan).</summary>
    public const float WaveIntervalSeconds = 150f;
    /// <summary>How long before it leaves a wave stands mustered (leashed) on the assembly ground, seconds.</summary>
    public const float MusterLeadSeconds = 20f;
    /// <summary>A wave leaves in chunks of this many Orks, one chunk every <see cref="ChunkGapSeconds"/> (about 170 an minute, under the gate's measured 214), so the 12 m gate is never jammed by a whole wave arriving at once (measured: a 300-Ork rush jammed it and 31 % of the Orks stood without headway).</summary>
    public const int ChunkSize = 20;
    /// <summary>A wave musters only at the assembly sources within this many metres of the ramp's centre line (the ramp is 30 m wide).</summary>
    public const float MusterHalfWidth = 46f;
    public const float ChunkGapSeconds = 6f;
    /// <summary>The waves' sizes (Orks); a later wave is as big as the last listed.</summary>
    public static readonly int[] WaveSizes = [300, 450, 600];
    /// <summary>The waves scheduled inside <see cref="SurviveSeconds"/> (the HUD's "OF n").</summary>
    public static int ScheduledWaves
    {
        get
        {
            int n = 0;
            for (float t = FirstWaveSeconds; t < SurviveSeconds; t += WaveIntervalSeconds) n++;
            return n;
        }
    }
    /// <summary>The elite share of the HQ battle: this fraction of Musou's own curve (<see cref="TfwBattleRules.EliteShare"/>).</summary>
    public const float EliteProgress = 0.75f;
    /// <summary>Boss Orks per human the wave from <see cref="BossWave"/> on brings (the third).</summary>
    public const int BossNobsPerHuman = 1, BossWave = 3;

    /// <summary>The Guard squads that start at each position's posts (W, K, V); the Guardsmen are not replaced.</summary>
    public static readonly int[] SquadsAt = [2, 1, 1];

    /// <summary>A stage-1 or stage-2 source never spawns within this many metres of a human, nor while a human is within <see cref="SourceSightMetres"/> (our stand-in for "in his sight": the courtyard and the hall are open to each other).</summary>
    public const float SourceClearanceMetres = 30f, SourceSightMetres = 60f;
    /// <summary>The share of a wave that comes from the stages' sources once they are open (each open stage takes this share of it).</summary>
    public const float StageShare = 0.25f;
    /// <summary>Orks that pour in at an opened stage's sources at the moment its position falls.</summary>
    public const int BreachFlood = 30;
    /// <summary>The XP every eligible player is paid for each position still held when the defence is won (plan section 5: HQ stage held 400; paid through <see cref="WarfrontProgressionRuntime.CaptureXp"/>).</summary>
    public const int HeldXp = WarfrontProgressionRuntime.CaptureXp;

    /// <summary>The size of wave number <paramref name="wave"/> (1 based).</summary>
    public static int WaveSize(int wave) => WaveSizes[Math.Clamp(wave - 1, 0, WaveSizes.Length - 1)];

    /// <summary>The battle's second at which wave <paramref name="wave"/> (1 based) leaves.</summary>
    public static float WaveLeaves(int wave) => FirstWaveSeconds + WaveIntervalSeconds * (wave - 1);

    /// <summary>The message ids of the Battle section.</summary>
    public const byte MessageNone = 0, MessageWallFell = 1, MessageKeepFell = 2, MessageHolds = 3, MessageFallen = 4, MessageWave = 5;
    /// <summary>The kinds of the Battle section.</summary>
    public const byte KindNode = 0, KindHq = 1, KindFortress = 2;
}
