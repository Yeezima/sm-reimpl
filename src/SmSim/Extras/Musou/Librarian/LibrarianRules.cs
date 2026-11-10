namespace SmSim.Extras.Musou;

/// <summary>
/// The Librarian's numbers (Musou Mode only; the owner's brief of 2026-11-07, docs/musou-expansion.md "Librarian"; spec/behavior.md "Musou Mode: the Librarian"). Every value is **ours** (kind: ours / estimate): the
/// original has no playable psyker. Times in seconds, distances in metres, angles in degrees. All of them sit here so a playtest retunes one file. Hashed into the config only through <see cref="MusouSim.Version"/>.
/// <para>
/// <b>Binding</b> (the existing buttons, no new input bit; the Librarian carries no gun, so these are free): Fire (held) = chain lightning, Reload (press) = Psychic Shockwave. Melee is the
/// ordinary melee; Grenade throws his equipment as any marine's (the owner, 2026-11-10: the Warp Pull is removed and "allow the librarian to use all equipment items that other classes can use
/// (frag grenades, blind grenades, stims)").
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
    /// <summary>A Shockwave adds this much (it equals 10 s of lightning: keep the ratio if one changes).</summary>
    public const float ShockwaveStress = 50f;
    /// <summary>Stress falls this much a second once <see cref="CoolDelay"/> has passed without casting (8 s from full).</summary>
    public const float CoolPerSecond = 12.5f, CoolDelay = 1.0f;
    /// <summary>A melee swing that strikes at least one enemy takes this much off (once a swing, however many it hits).</summary>
    public const float MeleeRelief = 6f;
    /// <summary>Seconds before the next Shockwave after a cast.</summary>
    public const float ShockwaveCooldown = 12f;

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

    // ---- Psychic Shockwave ----
    /// <summary>
    /// Everything within <see cref="ShockwaveRadius"/> (flat) and <see cref="ShockwaveVerticalReach"/> (up or down) takes <see cref="ShockwaveDamage"/> (the owner, 2026-11-10: "a range of 60m and massively
    /// increased effect and damage", his pick of 300: it kills every ordinary Ork, 'Ard Boy, Stormboy and Chaos Marine; a Nob keeps half; it was 9 m and 40). Ordinary Orks still standing are thrown between
    /// <see cref="ThrowMin"/> and <see cref="ThrowMax"/> over <see cref="ThrowSeconds"/>, elites between <see cref="EliteThrowMin"/> and <see cref="EliteThrowMax"/> (they were not moved), and a boss is
    /// staggered where he stands (the Warboss and the Boss Orks are stagger-immune: hyper armour, so only the Weirdboy reels). Distances are the owner's picks; the vertical reach and the throw time are ours.
    /// </summary>
    public const float ShockwaveRadius = 60f, ShockwaveDamage = 300f, ShockwaveVerticalReach = 15f;
    public const float ThrowMin = 40f, ThrowMax = 50f, EliteThrowMin = 15f, EliteThrowMax = 20f, ThrowSeconds = 1.2f;

    /// <summary>
    /// The pass 2 numbers, for the restore switches (<see cref="LibrarianVariant"/>, <c>SM_LIBRARIAN_OLD=shockwave,pull</c>): the Shockwave of 9 m (4 m up or down), 40 damage, ordinary Orks thrown 20 to 25 m over
    /// 1.0 s, elites and bosses not moved; the Warp Pull on Grenade: a cone of 30 degrees half-angle and 25 m, ordinary Orks to 2 m over 0.4 s, elites 35 % of the way, bosses immune, cooldown 8 s, stress 50.
    /// </summary>
    public static class Pass2
    {
        public const float ShockwaveRadius = 9f, ShockwaveDamage = 40f, ShockwaveVerticalReach = 4f, ThrowMin = 20f, ThrowMax = 25f, ThrowSeconds = 1.0f;
        public const float PullConeDegrees = 30f, PullRange = 25f, PullStopDistance = 2f, ElitePullShare = 0.35f, PullSeconds = 0.4f, PullCooldown = 8f, PullStress = 50f;
    }

    /// <summary>How the Shockwave treats an enemy: ordinary (thrown far), elite (thrown a shorter way) or boss (staggered in place, when he can be).</summary>
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
