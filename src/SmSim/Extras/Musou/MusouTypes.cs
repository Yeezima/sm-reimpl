namespace SmSim.Extras.Musou;

/// <summary>
/// One kind of Musou enemy's numbers (design section B; MU1). Hit points from the data where the design names them (the Boyz' 85), every other value ours
/// (kind: ours, to refine in playtests). Distances in metres, times in seconds, speeds in metres a second, damage in the players' health and shield points.
/// </summary>
/// <param name="Health">Hit points (a player's weapon damage takes them off one for one, at the weapon's body or head multiplier).</param>
/// <param name="Speed">Run speed.</param>
/// <param name="Radius">Body radius (separation, the players' shots).</param>
/// <param name="Height">Body height (the players' shots: a capsule to below the head, a head sphere at the top).</param>
/// <param name="Points">Score points for a KO (before the chain multiplier).</param>
public sealed record MusouTypeSpec(MusouEnemyType Type, float Health, float Speed, float Radius, float Height, int Points)
{
    /// <summary>Damage of one melee strike (0: no melee).</summary>
    public float MeleeDamage { get; init; }
    /// <summary>Seconds from one strike's start to the next (wind-up, strike and recovery).</summary>
    public float MeleePeriod { get; init; } = 1.4f;
    /// <summary>The telegraphed wind-up before the strike lands (design: 0.5 s).</summary>
    public float MeleeWindUp { get; init; } = 0.5f;
    /// <summary>Feet-to-feet reach of the strike, horizontally (checked when it lands, so a player can step out of it).</summary>
    public float MeleeRange { get; init; } = 1.6f;
    /// <summary>Half-angle of the strike's cone around the enemy's facing, checked when it lands.</summary>
    public float MeleeConeDegrees { get; init; } = 60f;
    /// <summary>A strike that lands also staggers (the core's stagger reaction, when the config has it).</summary>
    public bool MeleeStaggers { get; init; }

    /// <summary>Damage of one ranged round (0: no ranged attack).</summary>
    public float RangedDamage { get; init; }
    /// <summary>Rounds in one burst.</summary>
    public int RangedRounds { get; init; } = 3;
    /// <summary>Seconds between a burst's rounds.</summary>
    public float RangedRoundInterval { get; init; } = 0.12f;
    /// <summary>Seconds from one burst's start to the next.</summary>
    public float RangedPeriod { get; init; } = 1.6f;
    /// <summary>The distance band it shoots from (it holds there: closer it backs off, farther it closes in).</summary>
    public float RangedMin { get; init; } = 12f;
    public float RangedMax { get; init; } = 20f;
    /// <summary>Chance a round hits at <see cref="RangedMin"/> and at <see cref="RangedMax"/> (linear between; no projectile, no spread).</summary>
    public float RangedHitNear { get; init; } = 0.45f;
    public float RangedHitFar { get; init; } = 0.25f;

    public bool Melee => MeleeDamage > 0f;
    public bool Ranged => RangedDamage > 0f;
    /// <summary>Melee and ranged (the Chaos Marine): melee within <see cref="HybridMeleeDistance"/>, else the bolter.</summary>
    public bool Hybrid => Melee && Ranged;
    /// <summary>A hybrid goes for melee within this distance of its target.</summary>
    public float HybridMeleeDistance { get; init; } = 6f;
}

/// <summary>
/// One wave (design section B): <see cref="Total"/> enemies released in all and at most <see cref="MaxAlive"/> at once (both at one player; scaled by the
/// director), mixed by <see cref="Mix"/> (a share for each type, released interleaved), plus <see cref="Nobs"/> Nobs spread through the wave (not scaled).
/// </summary>
public sealed record MusouWaveSpec(int Total, int MaxAlive, IReadOnlyList<(MusouEnemyType Type, float Share)> Mix, int Nobs = 0);

/// <summary>
/// The Musou tuning (design sections B and E; MU1). <see cref="Default"/> holds the design's numbers; the rest are ours (kind: ours). It rides on the match's
/// <see cref="SimConfig.MusouRules"/> (loaded by <c>SmContent.Extras.Musou.MusouTuning</c>); a world without it uses <see cref="Default"/>. Hashed into the
/// config only through <see cref="MusouSim.Version"/> (raise it when a value here changes).
/// </summary>
public sealed record MusouRules
{
    /// <summary>The enemy kinds, indexed by <see cref="MusouEnemyType"/>.</summary>
    public IReadOnlyList<MusouTypeSpec> Types { get; init; } = DefaultTypes;
    /// <summary>The waves in order; the Warboss comes when the last one has released all its enemies.</summary>
    public IReadOnlyList<MusouWaveSpec> Waves { get; init; } = DefaultWaves;

    /// <summary>Wave sizes are multiplied by 1 + this x (players - 1) (design: 0.5).</summary>
    public float ScalePerExtraPlayer { get; init; } = 0.5f;
    /// <summary>Most enemies alive at once whatever the scale (design: 400).</summary>
    public int HardCap { get; init; } = 400;
    /// <summary>Seconds of break between waves (design: 20); health and ammunition are refilled at its start.</summary>
    public float BreakSeconds { get; init; } = 20f;
    /// <summary>Spawn gates (design: 8).</summary>
    public int Gates { get; init; } = 8;
    /// <summary>A gate releases only while every living player is at least this far (design: 20 m).</summary>
    public float GateMinDistance { get; init; } = 20f;
    /// <summary>Most releases in one tick (design: 6).</summary>
    public int ReleasesPerTick { get; init; } = 6;
    /// <summary>Seconds a released enemy stands before it moves (ours).</summary>
    public float SpawnSeconds { get; init; } = 0.5f;
    /// <summary>Seconds a dead enemy's slot is kept (dying) before it is freed (ours; the client plays its death meanwhile).</summary>
    public float DyingSeconds { get; init; } = 2f;
    /// <summary>The shared lives pool: this many with one player, plus <see cref="LivesPerExtraPlayer"/> for each further one (design: 3 + 2).</summary>
    public int Lives { get; init; } = 3;
    public int LivesPerExtraPlayer { get; init; } = 2;

    /// <summary>Melee attackers on one player at once (design: 4); the rest circle.</summary>
    public int MeleeTokens { get; init; } = 4;
    /// <summary>Shooters on one player at once (design: 6); the rest hold their band without firing.</summary>
    public int ShooterTokens { get; init; } = 6;
    /// <summary>The ring melee enemies without a token circle their target on (design: 3 to 6 m; each enemy its own radius).</summary>
    public float CircleMin { get; init; } = 3f;
    public float CircleMax { get; init; } = 6f;
    /// <summary>Within this distance of its target (horizontally, about one floor) an enemy seeks it directly instead of following the flow (design: 6 m).</summary>
    public float DirectSeekDistance { get; init; } = 6f;
    /// <summary>A melee enemy asks for a token within this distance of its target (ours), and gives it back beyond twice it.</summary>
    public float MeleeTokenDistance { get; init; } = 8f;

    /// <summary>Flow-field node expansions per tick (design: 20 000) and seconds between refreshes (design: about 0.5 s).</summary>
    public int FlowExpansionsPerTick { get; init; } = 20_000;
    public float FlowRefreshSeconds { get; init; } = 0.5f;
    /// <summary>Line-of-sight raycasts per tick for the shooters (design: 16), and ticks one stays valid (ours).</summary>
    public int LosChecksPerTick { get; init; } = 16;
    public int LosValidTicks { get; init; } = 15;
    /// <summary>Separation: the hash's cell (design: 1 m), neighbours considered (design: 8), the ring kept around players (design: 0.9 m, plus the enemy's radius).</summary>
    public float SeparationCell { get; init; } = 1f;
    public int MaxNeighbours { get; init; } = 8;
    public float PlayerRing { get; init; } = 0.9f;
    /// <summary>How hard overlapping enemies push apart (metres a second per metre of overlap; ours).</summary>
    public float SeparationStrength { get; init; } = 8f;

    /// <summary>KOs closer than this chain (design: 2 s); each chained KO raises the multiplier by <see cref="ChainStep"/>, up to <see cref="ChainMax"/> (design: x3).</summary>
    public float ChainSeconds { get; init; } = 2f;
    public float ChainStep { get; init; } = 0.2f;
    public float ChainMax { get; init; } = 3f;

    /// <summary>The Warboss's hit points at one player, times the wave scale (design: 4000) and its model scale (design: 1.25).</summary>
    public float BossHealth { get; init; } = 4000f;
    /// <summary>The Warboss's body and model scale: 1.6 (owner, 2026-11-01: "always a large orc, never small"; was 1.25; kind: ours). The client draws the largest Ork body at this scale.</summary>
    public float BossScale { get; init; } = 1.6f;

    /// <summary>The players' attacks on the enemies (MU3: the cleave, the staggers, the knockbacks; <see cref="MusouCombatRules"/>).</summary>
    public MusouCombatRules Combat { get; init; } = MusouCombatRules.Default;
    /// <summary>The Warboss's abilities (MU3; <see cref="MusouBossRules"/>).</summary>
    public MusouBossRules Boss { get; init; } = MusouBossRules.Default;
    /// <summary>
    /// The stranded-enemy failsafe (ours): an enemy in play without headway toward a player for this long (s) is moved to a gate clear of the players;
    /// still stranded after <see cref="StrandedRelocations"/> moves it is removed and counted as a KO for nobody (so a wave always ends). The Warboss is only
    /// ever moved.
    /// </summary>
    public float StrandedSeconds { get; init; } = 8f;
    public int StrandedRelocations { get; init; } = 1;

    /// <summary>Test aid (ours): the enemies' damage to players is multiplied by this (1 in a match; 0: they never hurt).</summary>
    public float PlayerDamageScale { get; init; } = 1f;

    /// <summary>The tuning of one type.</summary>
    public MusouTypeSpec Of(MusouEnemyType type) => Types[(int)type];

    static readonly MusouTypeSpec[] DefaultTypes =
    [
        // Boy (choppa), boy_melee: 85 hit points (data); the rest ours.
        new(MusouEnemyType.Boy, 85f, 5.2f, 0.4f, 1.9f, 10) { MeleeDamage = 8f, MeleePeriod = 1.4f },
        // Shoota Boy, boy_ranged: 85 (data); holds 12-20 m, 3-damage rounds in bursts of 3 (design); a weak bash up close.
        new(MusouEnemyType.ShootaBoy, 85f, 4.8f, 0.4f, 1.9f, 10) { RangedDamage = 3f, RangedRounds = 3, RangedPeriod = 1.6f, RangedMin = 12f, RangedMax = 20f },
        // 'Ard Boy, boy_melee_pro: 170 (design), slower.
        new(MusouEnemyType.ArdBoy, 170f, 4.4f, 0.45f, 1.95f, 20) { MeleeDamage = 10f, MeleePeriod = 1.5f },
        // Gretchin: 30 (design), fast and frail.
        new(MusouEnemyType.Gretchin, 30f, 6.2f, 0.3f, 1.2f, 5) { MeleeDamage = 4f, MeleePeriod = 1.0f, MeleeRange = 1.3f },
        // Nob: 600 (design), a heavy staggering swing.
        new(MusouEnemyType.Nob, 600f, 4.4f, 0.6f, 2.3f, 100) { MeleeDamage = 25f, MeleePeriod = 2.0f, MeleeRange = 2.2f, MeleeStaggers = true },
        // Chaos Marine (the PvP model): 250 (design), melee and a bolter.
        new(MusouEnemyType.ChaosMarine, 250f, 4.8f, 0.5f, 2.1f, 50)
        {
            MeleeDamage = 15f, MeleePeriod = 1.4f, MeleeRange = 1.8f,
            RangedDamage = 5f, RangedRounds = 3, RangedPeriod = 1.8f, RangedMin = 8f, RangedMax = 25f, RangedHitNear = 0.4f, RangedHitFar = 0.2f,
        },
        // Warboss: a scaled Nob for now (MU3: the slam, the charge, the Waaagh!, the enrage); health from BossHealth.
        new(MusouEnemyType.Warboss, 4000f, 4.6f, 0.75f, 2.9f, 2000) { MeleeDamage = 40f, MeleePeriod = 2.2f, MeleeRange = 2.8f, MeleeStaggers = true },
    ];

    static readonly MusouWaveSpec[] DefaultWaves =
    [
        // Totals and caps from the design; the mixes ours.
        new(80, 80, [(MusouEnemyType.Boy, 0.7f), (MusouEnemyType.ShootaBoy, 0.3f)]),
        new(120, 120, [(MusouEnemyType.Boy, 0.55f), (MusouEnemyType.ShootaBoy, 0.3f), (MusouEnemyType.ArdBoy, 0.15f)]),
        new(160, 160, [(MusouEnemyType.Boy, 0.6f), (MusouEnemyType.ShootaBoy, 0.25f), (MusouEnemyType.ArdBoy, 0.15f)], Nobs: 2),
        new(220, 200, [(MusouEnemyType.Boy, 0.45f), (MusouEnemyType.ShootaBoy, 0.25f), (MusouEnemyType.ArdBoy, 0.15f), (MusouEnemyType.ChaosMarine, 0.15f)], Nobs: 3),
        new(300, 250, [(MusouEnemyType.Boy, 0.45f), (MusouEnemyType.ShootaBoy, 0.2f), (MusouEnemyType.ArdBoy, 0.15f), (MusouEnemyType.ChaosMarine, 0.2f)], Nobs: 4),
    ];

    // Last: static fields initialise in order, and the default needs the tables above.
    /// <summary>The design's numbers.</summary>
    public static MusouRules Default { get; } = new();
}
