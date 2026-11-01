namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The Guardsmen's numbers (Musou Mode, design section E; spec "Musou Mode: Guardsmen"). The design's slice values (ours), but for the health: its 40 lost the 6 squads
/// against 60 Boyz (TF3's measure), so the health is the install's. <c>ig_coop_guardsman</c>'s others (run 6.25 m/s, walk 3.6 m/s; lasgun damage 1, clip 40, range 75) are in the spec
/// for the owner to choose from. A change here raises <see cref="TfwRules.Version"/>. Distances in metres, times in seconds, speeds in metres a second.
/// </summary>
public sealed record GuardRules
{
    /// <summary>A Guardsman's hit points (the install's <c>ig_coop_guardsman</c>; the design's 40 failed its own target).</summary>
    public float Health { get; init; } = 85f;
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
    /// <summary>A shot's damage.</summary>
    public float Damage { get; init; } = 5f;
    /// <summary>Seconds a dead man's slot is kept for the client's death.</summary>
    public float DyingSeconds { get; init; } = 2f;

    /// <summary>The design's numbers.</summary>
    public static GuardRules Default { get; } = new();
}
