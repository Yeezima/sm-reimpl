using SmSim.Extras.Musou;

namespace SmSim.Extras.Warfront;

/// <summary>
/// Warfront's Guardsmen line: hold, fall back, rally (owner brief 2026-11-24 rev 1, approved 2026-11-25; design state/design-guardsmen-1125.md with the owner's answers of 2026-11-25). Every value is ours
/// (spec "Added content: Warfront Guardsmen", subsection "Hold, fall back, rally"), kind ours, confidence estimate (low) unless noted. Distances in metres, times in seconds, pressure in weighted enemies.
/// Lane battles only (Ork and Chaos stages; never the HQ defence, the fortress, Musou Mode or Classic).
/// </summary>
public static class WarfrontLineRules
{
    /// <summary>The restore switch: environment <c>SM_WF_GUARD_AI_OLD=1</c> gives the Guardsmen of 2026-11-20 (rows and rotation, the mean-bearing approach, retreats to the base, the proximity rally, proportional reinforcements, no vox).</summary>
    public static readonly bool Old = Environment.GetEnvironmentVariable("SM_WF_GUARD_AI_OLD") == "1";

    // Pressure and men
    /// <summary>The push's reach round a point (flat; within the point's reach up and down): the existing <c>TfwBattleRules.ThreatRadius</c>.</summary>
    public const float PushRadius = 40f;
    /// <summary>The men of a point stand within its radius plus this (the rows reach past it: <see cref="WarfrontGuardRules.GoalSlack"/>).</summary>
    public const float MenSlack = 14f;
    /// <summary>Aggregate garrison men that count as one healthy squad.</summary>
    public const int AggregatePerHealthy = 10;
    /// <summary>Pressure weights: fodder 1, an elite 2.5, a boss 8.</summary>
    public const float FodderWeight = 1f, EliteWeight = 2.5f, BossWeight = 8f;

    /// <summary>An enemy's weight in the pressure on a point: the Boss Orks 8; 'Ard Boy, Nob, Big Shoota Nob, Weirdboy (and Chaos's Chaos Marine in the 'Ard slot) 2.5; every other 1.</summary>
    public static float Weight(MusouEnemyType t) => MusouBossOrks.Is(t) || t == MusouEnemyType.Warboss ? BossWeight
        : t is MusouEnemyType.ArdBoy or MusouEnemyType.Nob or MusouEnemyType.BigShootaNob or MusouEnemyType.Weirdboy or MusouEnemyType.ChaosMarine ? EliteWeight : FodderWeight;

    // The line machine
    /// <summary>Collapse: pressure at least this many times the men (with no healthy squad, no relief, no lockout).</summary>
    public const float CollapseRatio = 2f;
    /// <summary>Overwhelmed: pressure at least this many times the men collapses the line even while a squad is still healthy (an elite-led push; fodder at equal numbers does not reach it). Ours, set by the G1 trial: with the
    /// design's rule alone (every squad battered) a 0.6-elite push killed the squads in the 4 s confirmation and no line ever withdrew.</summary>
    public const float OverwhelmRatio = 6f;
    /// <summary>The overwhelm rule applies only with at most this many healthy squads left (round 2 trials: with several healthy squads a fall back gave a point the old line held).</summary>
    public const int OverwhelmHealthyMost = 1;
    /// <summary>Relief: a healthy squad or a Guard army bound for the point that is this many seconds away or less (at <see cref="ReliefSpeed"/>).</summary>
    public const float ReliefSeconds = 15f, ReliefSpeed = 3.6f;
    /// <summary>A collapse confirmed this many consecutive seconds is a fall back.</summary>
    public const int BreakSeconds = 4;
    /// <summary>The fewest seconds withdrawn before a rally.</summary>
    public const int MinWithdrawnSeconds = 8;
    /// <summary>The push is broken while the pressure stays at or below max(<see cref="BrokenFloor"/>, <see cref="BrokenShare"/> x the pressure at the break) for <see cref="BrokenSeconds"/>.</summary>
    public const float BrokenFloor = 6f, BrokenShare = 0.25f;
    public const int BrokenSeconds = 5;
    /// <summary>
    /// Lead 42 (2026-11-27, round 3; ours, low): a push counts as broken for an automatic rally (a line's record and a retreating Guard army alike) only while the approach ring is quiet too: the weighted enemies
    /// within <see cref="RallyRingRadius"/> (the sectors' ring, the same weights) at or below the same floor. Before it a garrison whose losses came from a flank army 40 to 70 m out rallied back into it 8 s later.
    /// </summary>
    public const bool RallyRingQuiet = true;
    public const float RallyRingRadius = WarfrontGuardRules.ApproachRadius;
    /// <summary>After re-forming, no new withdrawal from the point for this long.</summary>
    public const int RallyLockoutSeconds = 45;
    /// <summary>A rallying squad that has not reached its point after this long is released where it stands (as a field squad posted there).</summary>
    public const int RallyGiveUpSeconds = 90;
    /// <summary>A rallying squad that stopped short of its point is sent again at most this often (G3 finding 2).</summary>
    public const int RallyResendSeconds = 5;

    // Approach and flank
    /// <summary>Sectors of the approach (12 of 30 degrees) of the enemies within <see cref="WarfrontGuardRules.ApproachRadius"/>.</summary>
    public const int Sectors = 12;
    /// <summary>The approach turns only after the main bearing is off by more than <see cref="WarfrontGuardRules.ApproachTurnDegrees"/> this many steps in a row.</summary>
    public const int TurnSteps = 3;
    /// <summary>A flank is a block of sectors whose centre is at least this far from the main bearing.</summary>
    public const float FlankDegrees = 75f;
    /// <summary>A flank's block weighs at least this for <see cref="FlankSeconds"/> to be sustained; it is cleared at <see cref="FlankClearWeight"/> or less for <see cref="FlankClearSeconds"/>.</summary>
    public const float FlankWeight = 8f, FlankClearWeight = 3f;
    /// <summary>Lead 42 (2026-11-26): a flank must also weigh at least this share of the main block, so a crowd round the whole point does not raise Flank all the time (ours, low).</summary>
    public const float FlankMainShare = 0.35f;
    public const int FlankSeconds = 4, FlankClearSeconds = 6;
    /// <summary>Squads that answer a flank: one, two when the point has <see cref="FlankTwoAt"/> squads or more.</summary>
    public const int FlankSquadsMost = 2, FlankTwoAt = 6;
    /// <summary>The flank slot stands this share of the point's radius along the flank bearing.</summary>
    public const float FlankSlotShare = 0.5f;
    /// <summary>A flank squad is swapped (battered) at most once every this many seconds.</summary>
    public const int FlankSwapSeconds = 10;

    // Cramped ground
    /// <summary>A point is cramped when at least this share of its slots are cut to half their forward offset or less.</summary>
    public const float CrampedShare = 0.5f;
    /// <summary>Firing positions: searched by path up to the radius plus <see cref="MenSlack"/>, one node per <see cref="FiringCell"/> m cell with at least <see cref="FiringClearance"/> m off the walls, at most <see cref="FiringCandidates"/>.</summary>
    public const float FiringCell = 3f, FiringClearance = 1.5f;
    public const int FiringCandidates = 48;
    /// <summary>The sight test: from the eye over a candidate to the chest over a spot this far out along the approach.</summary>
    public const float FiringSightMetres = 20f;
    /// <summary>Squads at firing positions stand at least this far apart.</summary>
    public const float FiringSpacing = 6f;

    // Reinforce
    /// <summary>A point's need for reinforcements is weighted by 1 + <see cref="ReinforceThreatWeight"/> x min(1, pressure / <see cref="ReinforceThreatFull"/>); a Breaking or Withdrawn point counts as fully threatened.</summary>
    public const float ReinforceThreatWeight = 2f, ReinforceThreatFull = 40f;
    /// <summary>The Reinforcements line plays only for a point with this pressure or more, a Breaking one, or one Withdrawn in the last <see cref="ReinforceVoxWithdrawnSeconds"/>.</summary>
    public const float ReinforceVoxPressure = 20f;
    public const int ReinforceVoxWithdrawnSeconds = 20;
}
