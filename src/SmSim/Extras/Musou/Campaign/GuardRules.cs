namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The Guardsmen's numbers (Musou Mode, design section E; spec "Musou Mode: Guardsmen"). The design's slice values (ours), but for the health: its 40 lost the 6 squads
/// against 60 Boyz (TF3's measure), so the health is the install's; the health and the shot's damage are <see cref="Strength"/> times (4.5 from the night batch of 2026-11-06, 0.875 since 2026-11-08). <c>ig_coop_guardsman</c>'s others (run 6.25 m/s, walk 3.6 m/s; lasgun damage 1, clip 40, range 75) are in the spec
/// for the owner to choose from. A change here raises <see cref="TfwRules.Version"/>. Distances in metres, times in seconds, speeds in metres a second.
/// </summary>
public sealed record GuardRules
{
    /// <summary>
    /// The scale over the install's <c>ig_coop_guardsman</c> 85 hit points and the design's 5 a shot. The owner, 2026-11-08: "near clash balance should match far clash balance": the far clash
    /// is even man for man (<see cref="TfwClashRules.GuardWorth"/> 1), so the field's six squads (60 men) hold a point against 60 real Orks about half the time (<c>GuardHoldTrialTests</c>:
    /// 10 of 20 seeds at 0.875; 0.85 held 7, 0.9 15, 1 18). It was 4.5 (the night batch of 2026-11-06: 60 held against a full crowd of 400 half the time) until then. An estimate (ours).
    /// </summary>
    public const float Strength = 0.875f;
    /// <summary>A Guardsman's hit points: the install's 85 x <see cref="Strength"/>.</summary>
    public float Health { get; init; } = 85f * Strength;
    /// <summary>A Guardsman's speed; the squad's place in the world moves at <see cref="SquadSpeedScale"/> of it, so the men keep up.</summary>
    public float Speed { get; init; } = 4.5f;
    public float SquadSpeedScale { get; init; } = 0.9f;
    /// <summary>A squad gives up an order after this long without getting nearer to it (a place it cannot walk to).</summary>
    public float StallSeconds { get; init; } = 1f;
    /// <summary>On a way (M2): the last metres to an order are straight; a man this far ahead of his squad on the way slows to <see cref="WaitScale"/> of his speed.</summary>
    public float LastStraight { get; init; } = 4f;
    public float AheadOfSquad { get; init; } = 3f;
    public float WaitScale { get; init; } = 0.35f;
    /// <summary>A squad on the graph stops walking while an enemy is this near its centre (M2: it stands and fights, a front forms).</summary>
    public float HaltDistance { get; init; } = 8f;
    /// <summary>The formation's spacing (two rows of five).</summary>
    public float Spacing { get; init; } = 1.5f;
    /// <summary>Body radius for the men's separation, the hash's cell, how hard they push apart (m/s per metre of overlap) and the neighbours considered.</summary>
    public float Radius { get; init; } = 0.4f;
    public float SeparationCell { get; init; } = 2f;
    public float SeparationStrength { get; init; } = 6f;
    public int MaxNeighbours { get; init; } = 8;
    /// <summary>A squad looks for enemies this far from its centre every <see cref="QueryTicks"/> ticks, and keeps the nearest <see cref="SeenPerSquad"/>.</summary>
    public float Range { get; init; } = 40f;
    public int QueryTicks { get; init; } = 10;
    public const int SeenPerSquad = 16;
    /// <summary>A man fires every <see cref="FirePeriod"/> plus or minus <see cref="FireJitter"/> seconds.</summary>
    public float FirePeriod { get; init; } = 1f;
    public float FireJitter { get; init; } = 0.2f;
    /// <summary>Chance a shot hits: <see cref="HitNear"/> up to <see cref="HitNearDistance"/>, falling linearly to <see cref="HitFar"/> at <see cref="Range"/>. A shot needs a line of sight (M2).</summary>
    public float HitNear { get; init; } = 0.65f;
    public float HitNearDistance { get; init; } = 10f;
    public float HitFar { get; init; } = 0.2f;
    /// <summary>A shot's line of sight: from this high over his feet to this high over the enemy's, on the weapons mesh; a man looks at most at this many enemies for a shot.</summary>
    public float EyeHeight { get; init; } = 1.5f;
    public float ChestHeight { get; init; } = 1.2f;
    public int SightTries { get; init; } = 3;
    /// <summary>A shot's damage: the design's 5 x <see cref="Strength"/>.</summary>
    public float Damage { get; init; } = 5f * Strength;
    /// <summary>
    /// A hit on a charging Ork (<see cref="MusouSim.Charging"/>: a melee Ork sprinting in within 30 m) does this many times <see cref="Damage"/>: the line's volley into a charge (the owner, 2026-11-10: "Rebalance
    /// Guards" after the Orks' 1.2x pace and charge). Only the charge is met harder, so a mixed counterattack's balance (<c>GuardHoldTrialTests</c>, the far clash's even match) stays near where it was while six
    /// squads beat sixty charging Boyz again in about the old time (<c>GuardSimTests</c>). An estimate (ours); 1 is the rule before (server <c>SM_MUSOU_GUARD_BRACE=1</c>, also a tuning aid for other values).
    /// </summary>
    public float ChargeDamageScale { get; init; } = DefaultChargeDamageScale;
    public static readonly float DefaultChargeDamageScale =
        float.TryParse(Environment.GetEnvironmentVariable("SM_MUSOU_GUARD_BRACE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0f ? v : 1.5f;
    /// <summary>Seconds a dead man's slot is kept for the client's death.</summary>
    public float DyingSeconds { get; init; } = 2f;

    // musou/guard-spread (the owner, 2026-11-08: "guardsmen need to spread out much more when running to a point. after its captured they splinter and spread out around the point to defend it. on the
    // way to the point guardsmen get stuck on corners and walk in straight lines directly next to walls. they need to spread out."). Every number below is ours (an estimate for the playtest).

    /// <summary>The spread (true) or the old tight walk (false: the restore switch): wide open order on the move, the squads in their own lanes, the way kept off the walls, a stuck man's detour, and a squad sent to hold a point splitting into fire teams round it.</summary>
    public bool Spread { get; init; } = true;
    /// <summary>On the move: the gap between men abreast and between the two ranks (the second rank sits half a gap aside), and how far a man's place is shifted by his own roll, each way.</summary>
    public float MoveSpacing { get; init; } = 3.5f;
    public float MoveRankSpacing { get; init; } = 3f;
    public float MoveJitter { get; init; } = 0.75f;
    /// <summary>Over the squad's last this many metres to its order the open order closes, down to the standing spacing's share of it (<see cref="Spacing"/> / <see cref="MoveSpacing"/>).</summary>
    public float MoveTaper { get; init; } = 6f;
    /// <summary>On the move: squads go in three lanes (squad index mod 3: the middle, right, left) this far apart, so squads bound for one point do not walk in one file.</summary>
    public float LaneSpacing { get; init; } = 6f;
    /// <summary>A man's place (on the move or at his post) is pulled in towards his squad until it is at least this far from a wall or ledge (the graph's own body margin not counted).</summary>
    public float PlaceClearance { get; init; } = 1f;
    /// <summary>The way: among the steps that still get nearer, a step's clearance from the walls (up to <see cref="WayClearance"/>) counts this much against how directly it gets nearer (1: straight on).</summary>
    public float WayClearance { get; init; } = 2f;
    public float WayClearanceWeight { get; init; } = 1.5f;
    /// <summary>A man whose straight line to his place is checked on the graph every this many ticks (his line is walked: else he goes by the way).</summary>
    public int DirectCheckTicks { get; init; } = 6;
    /// <summary>A man who moved less than <see cref="StuckFraction"/> of the step he wanted for <see cref="StuckSeconds"/> is stuck: he goes by the way from where he stands (or sideways, with no way) for <see cref="DetourSeconds"/>.</summary>
    public float StuckSeconds { get; init; } = 0.5f;
    public float StuckFraction { get; init; } = 0.3f;
    public float DetourSeconds { get; init; } = 2f;
    /// <summary>Holding a point: a squad splits into this many fire teams spread over an arc of <see cref="HoldArcDegrees"/> centred on its own bearing from the point, at <see cref="HoldRadiusScale"/> of the point's radius, the men of a team <see cref="TeamSpacing"/> apart.</summary>
    public int HoldTeams { get; init; } = 3;
    public float HoldArcDegrees { get; init; } = 120f;
    public float HoldRadiusScale { get; init; } = 0.8f;
    public float TeamSpacing { get; init; } = 2f;
    /// <summary>A fire team looks this far round its spot for cover: the walkable place nearest <see cref="CoverEdge"/> metres from a wall (in its lee), reachable in a straight line from the point.</summary>
    public float CoverSearch { get; init; } = 3f;
    public float CoverEdge { get; init; } = 1f;

    /// <summary>The design's numbers.</summary>
    public static GuardRules Default { get; } = new();
}
