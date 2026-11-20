using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront;

/// <summary>
/// Warfront's Guardsmen (owner 2026-11-20: "add more guardsmen, make them fight in a 'defense in depth' style where there are grids of squads who cover each other from behind. buff guardsmen-ork balance to be 30-60 ork favored";
/// his answers: 30-60 ork favored means the Orks win about 2 of 3 even fights, the Guardsmen at about half the Orks' strength; twice today's numbers; in every Warfront battle that has Guardsmen). Every value is ours (spec
/// "Added content: Warfront Guardsmen"), applied to Warfront battles only: Musou Mode and the Classic modes keep <see cref="GuardRules.Default"/>. Distances in metres, times in seconds.
/// </summary>
public static class WarfrontGuardRules
{
    /// <summary>The restore switch: environment <c>SM_WF_GUARD_OLD=1</c> gives the Guardsmen as they were before the change (the old numbers, Musou's strength, no rows); the tools' before row, a test aid.</summary>
    public static readonly bool Old = Environment.GetEnvironmentVariable("SM_WF_GUARD_OLD") == "1";

    /// <summary>The multiplier of the Guardsmen's numbers: field squads, reserve squads, the HQ's posts, garrisons and reinforcements (the sim's 40 squads and the wire's 448 men stay).</summary>
    public static readonly int NumberScale = Old ? 1 : 2;


    /// <summary>
    /// The scale over <see cref="GuardRules.Strength"/> on a Guardsman's health and shot damage, so that equal numbers on open ground lose about 2 of 3 to the Orks (the trial of <c>WarfrontGuardBalanceTests</c>: 30 seeded
    /// fights of 60 Guardsmen against 60 Orks held a point 14 of 30 at 1 and 9 of 30 at 0.95; the sweep is in the spec). The Orks are untouched: only the Guardsmen's own numbers move.
    /// </summary>
    public static readonly float StrengthScale = Old ? 1f : 0.95f;

    /// <summary>A Guardsman's worth in a far clash (<see cref="TfwClashRules.GuardWorth"/> times this): the strength's square (health times damage), so the aggregate garrisons fight as the real squads do.</summary>
    public static readonly float ClashWorthScale = StrengthScale * StrengthScale;

    /// <summary>The Guardsmen's rules in Warfront.</summary>
    public static GuardRules Rules { get; } = new()
    {
        Health = 85f * GuardRules.Strength * StrengthScale,
        Damage = 5f * GuardRules.Strength * StrengthScale,
    };

    // Defence in depth: squads in a grid of rows facing the Ork approach on each held point (owner 2026-11-20). Row 0 is the front line; each row behind stands in the gaps of the one before it (every other row is
    // shifted half a squad), so its men fire between the front squads' men (a Guardsman's shot is a roll on a line of sight, never stopped by a friend: the gaps keep the line clear and show the cover).
    /// <summary>Squads in a row, rows at most, and the metres between the squads of a row and between rows.</summary>
    public const int SquadsPerRow = 3, MaxRows = 4;
    public const float SquadSpacing = 9f, RowSpacing = 9f;
    /// <summary>Within a squad: the men of a row abreast (a line of two ranks) this far apart, the ranks this far apart.</summary>
    public const float ManSpacing = 1.4f, RankSpacing = 1.6f;
    /// <summary>The front row stands this share of the point's radius toward the Orks' approach from its centre (the rows go back from there).</summary>
    public const float FrontShare = 0.5f;
    /// <summary>A squad is battered (falls back through the gap to the rear row, the squad behind stepping up) with this many men or fewer (3: a vantage team leaves its squad four), as long as another squad stands in the position's rows to take its place.</summary>
    public const int BatteredMen = 3;
    /// <summary>The approach is the Orks near the point (at least <see cref="ApproachOrks"/> of them within <see cref="ApproachRadius"/>) and else the Ork source's bearing; it turns only when the new bearing differs by this many degrees.</summary>
    public const int ApproachOrks = 6;
    public const float ApproachRadius = 70f, ApproachTurnDegrees = 30f;
    /// <summary>A squad counts as on its point this much beyond the point's radius (the rows reach past it).</summary>
    public const float GoalSlack = 14f;
}
