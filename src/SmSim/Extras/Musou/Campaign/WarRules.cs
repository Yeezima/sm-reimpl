namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The numbers of the war between the battles (design section C, kind ours, spec "the war", tuned by <c>WarSimTests</c> to the calibration targets) and the formulas over them.
/// Everything is in war seconds, unless named otherwise: one second of real time times <see cref="TimeScale"/>.
/// </summary>
public static class WarRules
{
    /// <summary>The real seconds between two <see cref="WarSim.Step"/> calls (the war runs at 1 Hz).</summary>
    public const double StepSeconds = 1.0;

    /// <summary>The time scale in the hub: the war waits for a player choosing, not for free.</summary>
    public const double HubTimeScale = 0.25;

    // The start of a run.
    public const double StartImperial = 100.0, StartOrk = 110.0;
    public const sbyte StartFrontA = 1, StartFrontB = 0, StartFrontC = 1;

    // The front: -1 is the HQ besieged, 3 the stronghold (the lane unlocks the final assault).
    public const sbyte FrontBesieged = -1, FrontStronghold = 3;

    /// <summary>The war seconds a whole advantage (1) takes to move the progress from 0 to a node (1).</summary>
    public const double ProgressSeconds = 30.0;

    public const double LordMultiplier = 1.25, HqMultiplier = 1.5;

    /// <summary>Losses a war second, per enemy strength point.</summary>
    public const double Attrition = 0.0002;

    public const double ImperialReinforcement = 0.03, ImperialCap = 200.0;
    public const double OrkReinforcement = 0.035, OrkCap = 250.0;

    /// <summary>The war seconds in which the Orks' reinforcement grows by its base rate (it doubles at this clock).</summary>
    public const double OrkGrowthSeconds = 1800.0;

    /// <summary>The war seconds a front holds its progress after a node changed hands.</summary>
    public const double GatherSeconds = 60.0;

    // A player's battle.
    public const double WinOrkFactor = 0.6, WinImperial = 20.0, WinGatherSeconds = 30.0;
    public const double LeftProgressBase = -0.2, LeftProgressPerSecured = 0.25, LeftProgressMax = 0.6;
    public const double LeftOrkPerKill = 0.05, LeftImperialPerGuard = 0.1;
    public const int Positions = 3;

    /// <summary>The war seconds a lord killed in a battle stays wounded (5 war-minutes).</summary>
    public const double LordWoundSeconds = 300.0;

    /// <summary>The values of <see cref="TfwLaneState.LordState"/>.</summary>
    public const byte LordPresent = 0, LordWounded = 1;

    // The final assault: per other lane at this front or better.
    public const sbyte FinalBonusFront = 2;
    public const float FinalBossHealthFactor = 0.85f;
    public const int FinalExtraSquads = 2;

    /// <summary>The war's time scale in a phase: 0 stops the clock (the final assault, the end).</summary>
    public static double TimeScale(TfwRunPhase phase) => phase switch
    {
        TfwRunPhase.Hub => HubTimeScale,
        TfwRunPhase.Travel or TfwRunPhase.Battle or TfwRunPhase.Pickup => 1.0,
        _ => 0.0,
    };

    /// <summary>The multiplier on the Imperial strength: a present lord, and the last stand at the HQ.</summary>
    public static double Multiplier(bool lordPresent, sbyte front) => (lordPresent ? LordMultiplier : 1.0) * (front == FrontBesieged ? HqMultiplier : 1.0);

    /// <summary>The advantage, -1 (all Orks) to 1 (all Imperial); 0 when both sides are spent.</summary>
    public static double Advantage(double multiplier, double imperial, double ork)
    {
        double total = multiplier * imperial + ork;
        return total > 0.0 ? (multiplier * imperial - ork) / total : 0.0;
    }

    /// <summary>The Orks' reinforcement a war second at war clock <paramref name="clock"/>.</summary>
    public static double OrkReinforcementAt(double clock) => OrkReinforcement * (1.0 + clock / OrkGrowthSeconds);

    /// <summary>The lane's progress after a battle the player left or lost with <paramref name="secured"/> positions secured.</summary>
    public static double LeftProgress(int secured) => Math.Clamp(LeftProgressBase + LeftProgressPerSecured * Math.Clamp(secured, 0, Positions), -1.0, LeftProgressMax);

    /// <summary>Each position's owner bit at a front (bit set: Imperial): the chain P1, P2, P3 is held up to the front.</summary>
    public static byte OwnedMask(sbyte front) => (byte)((1 << Math.Clamp((int)front, 0, Positions)) - 1);
}
