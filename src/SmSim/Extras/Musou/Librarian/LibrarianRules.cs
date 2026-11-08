namespace SmSim.Extras.Musou;

/// <summary>
/// The Librarian's numbers (Musou Mode only; the owner's brief of 2026-11-07, docs/musou-expansion.md "Librarian"; spec/behavior.md "Musou Mode: the Librarian"). Every value is **ours** (kind: ours / estimate): the
/// original has no playable psyker. Times in seconds, distances in metres, angles in degrees. All of them sit here so a playtest retunes one file. Hashed into the config only through <see cref="MusouSim.Version"/>.
/// <para>
/// <b>Binding</b> (the existing buttons, no new input bit; the Librarian carries no gun and no grenade, so these are free): Fire (held) = chain lightning, Grenade (press) = Warp Pull,
/// Reload (press) = Psychic Shockwave. Melee is the ordinary melee.
/// </para>
/// <para><b>Psychic Ward</b> (a shield that soaks damage for stress): a future idea only. Nothing of it is built or reserved on the wire.</para>
/// </summary>
public static class LibrarianRules
{
    /// <summary>The class's key in <see cref="SimConfig.Classes"/> (appended after the others under Musou only, <see cref="MusouLoadout.Prepare"/>) and the wargear of its starting melee.</summary>
    public const string ClassName = "sm - librarian", PowerSwordWargear = "sm_mp_melee_powersword";

    // ---- Psychic Stress (shared by every power) ----
    /// <summary>The scale's top: at this all powers lock until the stress is back at 0.</summary>
    public const float StressMax = 100f;
    /// <summary>Chain lightning adds this much a second while held (so 20 s fills the scale).</summary>
    public const float LightningStressPerSecond = 5f;
    /// <summary>A Warp Pull and a Shockwave each add this much (each equals 10 s of lightning: keep the ratios if one changes).</summary>
    public const float PullStress = 50f, ShockwaveStress = 50f;
    /// <summary>Stress falls this much a second once <see cref="CoolDelay"/> has passed without casting (8 s from full).</summary>
    public const float CoolPerSecond = 12.5f, CoolDelay = 1.0f;
    /// <summary>A melee swing that strikes at least one enemy takes this much off (once a swing, however many it hits).</summary>
    public const float MeleeRelief = 6f;
    /// <summary>Seconds before the next Pull / Shockwave after a cast.</summary>
    public const float PullCooldown = 8f, ShockwaveCooldown = 12f;

    // ---- Chain lightning ----
    /// <summary>It reaches this far from the eye, to the enemy best lined up with the aim within <see cref="AimConeDegrees"/> (ours).</summary>
    public const float LightningRange = 30f, AimConeDegrees = 12f;
    /// <summary>It jumps to the nearest enemy not yet struck within this of the last one, up to <see cref="MaxTargets"/> struck in all.</summary>
    public const float JumpRadius = 8f;
    public const int MaxTargets = 40;
    /// <summary>The first enemy takes this a second; each jump takes <see cref="JumpFalloff"/> of the one before.</summary>
    public const float PrimaryDps = 90f, JumpFalloff = 0.85f;
    /// <summary>Enemies this far above or below the flat circle are out of reach of a jump (ours).</summary>
    public const float VerticalReach = 4f;
    /// <summary>Most links the wire carries a tick: every target struck since MusouSim 14 (the wire's 3-byte jumps, <c>LibrarianWire</c>; it was 16 at 12 bytes a link).</summary>
    public const int MaxDrawnLinks = MaxTargets;

    // ---- Warp Pull ----
    /// <summary>A cone in front of the Librarian: half-angle <see cref="PullConeDegrees"/> (the melee cone's convention; the brief's "30 degrees" read as the half-angle, ours) and this far.</summary>
    public const float PullConeDegrees = 30f, PullRange = 25f;
    /// <summary>An ordinary Ork is pulled to this far from the Librarian, an elite this share of his distance; bosses are immune. The pull takes <see cref="PullSeconds"/> (ours).</summary>
    public const float PullStopDistance = 2f, ElitePullShare = 0.35f, PullSeconds = 0.4f;

    // ---- Psychic Shockwave ----
    /// <summary>Everything within this takes <see cref="ShockwaveDamage"/> (kills only a Gretchin, 30 hp; a Boy has 85). Ordinary Orks are thrown between the two distances over <see cref="ThrowSeconds"/> (ours); elites and bosses are not moved.</summary>
    public const float ShockwaveRadius = 9f, ShockwaveDamage = 40f;
    public const float ThrowMin = 20f, ThrowMax = 25f, ThrowSeconds = 1.0f;

    /// <summary>How the powers treat an enemy: ordinary (pulled all the way, thrown), elite (pulled a part, not thrown) or boss (neither).</summary>
    public enum Tier { Ordinary, Elite, Boss }

    /// <summary>The tier of an enemy type. Bosses: the Warboss, the Boss Orks, the Weirdboy. Elites: Nobz and 'Ard Boyz. Everything else is ordinary. A new kind of Ork goes in here.</summary>
    public static Tier TierOf(MusouEnemyType type) =>
        type is MusouEnemyType.Warboss or MusouEnemyType.Weirdboy || MusouBossOrks.Is(type) ? Tier.Boss
        : type is MusouEnemyType.Nob or MusouEnemyType.ArdBoy ? Tier.Elite
        : Tier.Ordinary;

    /// <summary>Whether <paramref name="classIndex"/> is the Librarian in <paramref name="config"/>.</summary>
    public static bool IsLibrarian(SimConfig config, int classIndex) =>
        config.Extras.HasFlag(ContentExtras.Musou) && Loadouts.ClassOf(config, classIndex) is { } c && c.Name == ClassName;
}
