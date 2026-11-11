namespace SmSim.Extras.Musou;

/// <summary>
/// The roaming Boss Orks of Musou Mode (owner, 2026-11-06: "randomly spawning boss orks (they spawn with an elite retinue of elite ork enemies). the bosses have massively incresased hp, damage,
/// and are scaled up 3x from their usual size"; his answers in docs/musou-boss-orks-design.md). Four kinds of their own (<see cref="MusouEnemyType.BossBoy"/> to <see cref="MusouEnemyType.BossNob"/>),
/// each made from its base kind's numbers by <see cref="From"/>, so the wire, the shots and the moves see the size and the strength through the type table alone. Where they spawn and their
/// retinue: <c>TfwBattle.BossOrks.cs</c>. Every value is the owner's or ours (kind: ours, spec "Musou Mode: Boss Orks").
/// </summary>
public static class MusouBossOrks
{
    /// <summary>The body and model scale (the owner: "scaled up 3x from their usual size").</summary>
    public const float BodyScale = 3f;
    /// <summary>Health: the base kind's times this (the owner chose 10x) ...</summary>
    public const float HealthScale = 10f;
    /// <summary>... and never under this (the owner: "floor of 3000 hp").</summary>
    public const float HealthFloor = 3000f;
    /// <summary>Damage of a melee strike and of a ranged round: the base kind's times this (the owner chose 2x).</summary>
    public const float DamageScale = 2f * MusouBossRules.DangerScale;   // 3 since 2026-11-12 (owner: "more dangerous"; was 2, the owner's 2x of 2026-11-06)
    /// <summary>The melee reach: the base kind's times this (ours: a 3x arm; the full 3x would strike from beyond a player's own melee reach).</summary>
    public const float ReachScale = 1.5f;
    /// <summary>Run speed: the base kind's, at most this (ours: the 'Ard Boyz' and Nobs' pace, so the retinue keeps up with its boss).</summary>
    public const float MaxSpeed = 4.4f;
    /// <summary>Score points for the KO (ours; the Nob's 100 times 10).</summary>
    public const int Points = 1000;

    /// <summary>Whether <paramref name="type"/> is a Boss Ork.</summary>
    public static bool Is(MusouEnemyType type) => type is MusouEnemyType.BossBoy or MusouEnemyType.BossShootaBoy or MusouEnemyType.BossArdBoy or MusouEnemyType.BossNob;

    /// <summary>The kind a Boss Ork is made from (its look, sounds and blood); any other kind is its own.</summary>
    public static MusouEnemyType BaseOf(MusouEnemyType type) => type switch
    {
        MusouEnemyType.BossBoy => MusouEnemyType.Boy,
        MusouEnemyType.BossShootaBoy => MusouEnemyType.ShootaBoy,
        MusouEnemyType.BossArdBoy => MusouEnemyType.ArdBoy,
        MusouEnemyType.BossNob => MusouEnemyType.Nob,
        _ => type,
    };

    /// <summary>The Boss Ork made from <paramref name="type"/> (Boy, Shoota Boy, 'Ard Boy, Nob); null for any other kind.</summary>
    public static MusouEnemyType? BossOf(MusouEnemyType type) => type switch
    {
        MusouEnemyType.Boy => MusouEnemyType.BossBoy,
        MusouEnemyType.ShootaBoy => MusouEnemyType.BossShootaBoy,
        MusouEnemyType.ArdBoy => MusouEnemyType.BossArdBoy,
        MusouEnemyType.Nob => MusouEnemyType.BossNob,
        _ => null,
    };

    /// <summary>The kinds a Boss Ork is drawn from, in the order the spawner picks by (the owner: "any Ork type").</summary>
    public static readonly MusouEnemyType[] Kinds = [MusouEnemyType.BossBoy, MusouEnemyType.BossShootaBoy, MusouEnemyType.BossArdBoy, MusouEnemyType.BossNob];

    /// <summary>A Boss Ork's numbers from its base kind's <paramref name="b"/>: 3x the body, 10x the health (at least 3000), 2x the damage, 1.5x the reach, a strike that staggers.</summary>
    public static MusouTypeSpec From(MusouEnemyType boss, MusouTypeSpec b) =>
        b with
        {
            Type = boss,
            Health = MathF.Max(HealthFloor, b.Health * HealthScale),
            Speed = MathF.Min(b.Speed, MaxSpeed),
            Radius = b.Radius * BodyScale,
            Height = b.Height * BodyScale,
            Points = Points,
            MeleeDamage = b.MeleeDamage * DamageScale,
            MeleeRange = b.MeleeRange * ReachScale,
            MeleeStaggers = b.Melee || b.MeleeStaggers,
            RangedDamage = b.RangedDamage * DamageScale,
        };
}
