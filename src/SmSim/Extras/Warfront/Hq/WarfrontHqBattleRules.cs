using System.Numerics;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Fortress;

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
    /// <summary>The time between two waves leaving, seconds (0:45, 2:45, 4:45: designer review of W13b, ruling 2; the last one matters, from 5:45 it reached the wall as the clock ran out).</summary>
    public const float WaveIntervalSeconds = 120f;
    /// <summary>How long before it leaves a wave stands mustered (leashed) on the assembly ground, seconds.</summary>
    public const float MusterLeadSeconds = 20f;
    /// <summary>A wave leaves in chunks of this many Orks, one chunk every <see cref="ChunkGapSeconds"/> (240 a minute at most, the gate carries about 214 at best), so the 12 m gate is never jammed by a whole wave arriving at once (measured: a 300-Ork rush jammed it and 31 % of the Orks stood without headway).</summary>
    public const int ChunkSize = 20;
    /// <summary>A wave musters only at the assembly sources within this many metres of the ramp's centre line (the ramp is 30 m wide: 12 is inside it, the wave musters in depth; designer review of W13b, ruling 1; was 46, which sent the flanks into the ramp's side).</summary>
    public const float MusterHalfWidth = 12f;
    public const float ChunkGapSeconds = 5f;
    /// <summary>The distance between the rows a wave stands in on the assembly ground (ours).</summary>
    public const float MusterRowMetres = 8f;
    /// <summary>Most Orks the defence makes in one tick (the muster and the breach flood are spread: a whole wave in one tick was the tick spike; designer review ruling 4).</summary>
    public const int SpawnsPerTick = 10;
    /// <summary>The HQ's own alive cap (the lesser of this and <c>AliveCap(humans)</c>; ruling 2 and 4).</summary>
    public const int AliveCapHq = 400;
    /// <summary>The waves' sizes (Orks): 200, 300 and 400 (designer review ruling 2: what the gate can carry).</summary>
    public static readonly int[] WaveSizes = [200, 300, 400];
    /// <summary>The waves scheduled (the HUD's "OF n"): one for each size listed; none after the last.</summary>
    public static int ScheduledWaves => WaveSizes.Length;

    // The fall rule (ruling 2): inside the front position's ring the Orks' weight against the defenders' weight; the hold drains while the Orks outweigh, recovers otherwise.
    /// <summary>An Ork's weight; a Nob or bigger counts <see cref="BigOrkWeight"/>.</summary>
    public const int OrkWeight = 1, BigOrkWeight = 3;
    /// <summary>The defenders' weights: a standing human, a standing squad Marine, a Guardsman.</summary>
    public const int HumanWeight = 6, MarineWeight = 2, GuardsmanWeight = 1;
    /// <summary>The position's hold drains from 1 to 0 in this many seconds while the Orks outweigh the defenders, and recovers from 0 to 1 in <see cref="HoldRecoverSeconds"/> otherwise.</summary>
    public const float HoldDrainSeconds = 20f, HoldRecoverSeconds = 40f;
    /// <summary>Under this hold the HUD says the position is falling ("THE WALL IS FALLING").</summary>
    public const float HoldFallingBelow = 0.5f;
    /// <summary>The elite share of the HQ battle: this fraction of Musou's own curve (<see cref="TfwBattleRules.EliteShare"/>): 1, the top (was 0.75).</summary>
    public const float EliteProgress = 1f;
    /// <summary>Boss Orks per human the wave from <see cref="BossWave"/> on brings (the second; was the third).</summary>
    public const int BossNobsPerHuman = 1, BossWave = 2;

    /// <summary>The Guard squads that start at each position's posts (W, K, V); the Guardsmen are not replaced.</summary>
    public static readonly int[] SquadsAt = [2, 1, 1];
    /// <summary>The strength each Guard squad is spawned with at W, K and V (<c>GuardSim.SpawnSquad</c>'s own parameter; 1 is a full squad). W13c: the one step allowed without a ruling if the passive human still won after the rout rule.</summary>
    public static readonly float[] GuardStrengthAt = [1f, 1f, 1f];

    /// <summary>A stage-1 or stage-2 source never spawns within this many metres of a human, nor while a human is within <see cref="SourceSightMetres"/> (our stand-in for "in his sight": the courtyard and the hall are open to each other).</summary>
    public const float SourceClearanceMetres = 30f, SourceSightMetres = 60f;
    /// <summary>The height of an eye above the feet for the sight test, metres (the fortress's own: <c>WarfrontFortressBattleRules.EyeHeight</c>).</summary>
    public const float EyeHeight = WarfrontFortressBattleRules.EyeHeight;
    /// <summary>
    /// The sight test (W13c): true when the line from the first point to the second is not stopped by a wall; set by the server at the HQ landing from the stage's weapon collision (<c>GameServer.WarfrontHqSight</c>), the same ray as the fortress's. It is process-wide
    /// because the stage runs one battle per process (the same as the rules' <c>Target</c>); null means the 60 m stand-in (<see cref="SourceSightMetres"/>). Tests that set it must reset it.
    /// </summary>
    public static Func<Vector3, Vector3, bool>? Sight { get; set; }
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
