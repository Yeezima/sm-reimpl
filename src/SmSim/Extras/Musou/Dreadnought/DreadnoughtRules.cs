namespace SmSim.Extras.Musou;

/// <summary>
/// The Musou Dreadnought's numbers (Musou Mode only; added content; docs/musou-dreadnought-design.md, spec/behavior.md "Added content: Musou Dreadnought"). Speeds, the entrance and the names come from the install's
/// <c>sm_dreadnought</c> blueprint (<c>DreadnoughtData</c> in SmContent reads them and its parity tests pin each; kind: data). Health, regeneration and the capsule are the owner's decisions of 2026-11-14 and carry
/// kind **estimate** (refine register): the data's 8000 health and 250 a second make ordinary Orks harmless, so Musou gives him a pool of his own. Times in seconds, speeds in m/s, distances in metres.
/// </summary>
public static class DreadnoughtRules
{
    /// <summary>The class's key in <see cref="SimConfig.Classes"/> (appended after the Librarian under Musou only, <see cref="MusouLoadout.Prepare"/>), its role (new: a balanced join never swaps it) and its body's name.</summary>
    public const string ClassName = "sm - dreadnought", Role = "dreadnought", BodyName = "dreadnought";

    /// <summary>The install's wargear names of his two guns (kind: data); <c>SmContent.Extras.Musou.DreadnoughtWeapons</c> appends them to a Musou config.</summary>
    public const string CannonWargear = "dread_assault_cannon", MeltaWargear = "dread_melta";


    // ---- the buff of 2026-11-15 (owner: "buff the dreadnaught's weapons in musou mode") ----
    /// <summary>
    /// His arm is this many times as fast as the data's clips (owner, 2026-11-15: "melee attacks are 30% faster"; **owner's value**): every timing of every strike (the wind-up to the hit, the combo and chain windows, the move
    /// and ranged cancels, the length) is the data's divided by it, and the client plays the strike's clip at this rate so the hit lands on the same frame of the animation.
    /// </summary>
    public const float MeleeSpeed = 1.3f;

    /// <summary>
    /// The Dreadnought's melta, scaled up from the Tactical's (owner, 2026-11-15: "a larger scaled up version of the tactical marine melta (1.5x damage, faster reload, bigger range, still does full damage at max range, infinite ammo)").
    /// Damage x1.5 is the owner's; the reload at 0.6 of the Tactical's and the reach at 1.5 times are **estimates** (ours; the owner gave no numbers: refine register).
    /// </summary>
    public const float MeltaDamageScale = 1.5f, MeltaReloadScale = 0.6f, MeltaRangeScale = 1.5f;

    /// <summary>The wargear names that spend no ammunition in Musou Mode (his cannon, whose limit is its heat, and his melta; owner, 2026-11-15: "unlimited ammo", "infinite ammo").</summary>
    public static bool IsEndless(string? wargear) => IsCannon(wargear) || IsMelta(wargear);

    /// <summary>Whether <paramref name="wargear"/> is his cannon (the autocannon's numbers; every rule that treats it as the autocannon asks here, so a rename fails the tests loudly) or his melta.</summary>
    public static bool IsCannon(string? wargear) => string.Equals(wargear, CannonWargear, StringComparison.OrdinalIgnoreCase);
    public static bool IsMelta(string? wargear) => string.Equals(wargear, MeltaWargear, StringComparison.OrdinalIgnoreCase);

    // ---- movement (data: sm_dreadnought speeds, standing / aiming) ----
    public const float RunSpeed = 6.5f, StrafeSpeed = 6.5f, BackSpeed = 4.5f, SprintSpeed = 11f, WalkSpeed = 3f;
    public const float AimRunSpeed = 5f, AimStrafeSpeed = 5f, AimBackSpeed = 4f;

    // ---- health (the owner's pool, estimate) ----
    /// <summary>The data's health, regeneration delay and rate (8000, 5 s, 250 a second): what the original gives him, kept for the parity tests and the scaling below.</summary>
    public const float DataHealth = 8000f, DataRegenDelay = 5f, DataRegenRate = 250f;
    /// <summary>
    /// His Musou health (owner, 2026-11-14: his own pool, not the data's 8000; **estimate**): 2000 = ten times a marine's 200 (100 health and 100 armour) and two thirds of a Boss Ork's 3000 floor
    /// (<c>MusouBossOrks.HealthFloor</c>), so a sustained crowd can wear him down where the data's pool is immortal to it. No armour shield (data).
    /// </summary>
    public const float Health = 2000f, Shield = 0f;
    /// <summary>
    /// The regeneration delay is the data's 5 s; the rate is the data's 250 a second scaled by <see cref="Health"/> over <see cref="DataHealth"/> (3.125 % of his health a second: 62.5; **estimate**, "scaled consistently"),
    /// so he mends the same share of himself a second as the original's.
    /// </summary>
    public const float RegenDelay = DataRegenDelay, RegenRate = DataRegenRate * Health / DataHealth;

    // ---- the body (estimate: the data gives the melee hit volume, 1.8 m by 2.3 m, but no capsule) ----
    /// <summary>His walking capsule (owner, 2026-11-14: "a true big body, about 2 to 3 m"; **estimate**): 2 m across, 3 m tall. The Orks keep their ring, reach and hold at the edge of it (<see cref="MusouTarget.Body"/>).</summary>
    public const float CapsuleRadius = 1.0f, CapsuleHeight = 3.0f;

    // ---- the landing (owner, 2026-11-15) ----
    /// <summary>
    /// Metres between two Dreadnoughts at the landing (**estimate**, ours): 4 m, his 2 m capsule and a 2 m gap, so a squad of Dreadnoughts is not one pile (the owner: "Spread them out a bit if multiple players picked dreadnaught
    /// class"). The first stands at the landing zone's centre.
    /// </summary>
    public const float LandingSpacing = 4f;

    /// <summary>
    /// Where the <paramref name="j"/>-th Dreadnought (0-based, in id order) stands from the landing zone (the Imperial base), in metres (owner, 2026-11-15: "Make the dreadnaught spawn in the middle of the imperial base HQ at the
    /// start of the Musou game mode"): the first at the centre, the others alternating right and left of it along the base's width, <see cref="LandingSpacing"/> apart (0, +4, -4, +8, ...).
    /// </summary>
    public static System.Numerics.Vector3 LandingOffset(int j) => new(((j + 1) / 2) * (j % 2 == 1 ? 1f : -1f) * LandingSpacing, 0f, 0f);

    /// <summary>Metres of ground walked between two footfall blasts (**estimate**: the walk cycle's two footfalls in about 4 m).</summary>
    public const float FootfallStride = 2.2f;

    // ---- the sprint and the buff (data: dread_sprint, dread_buff) ----
    /// <summary>The sprint's longest run and the gap after it (data, seconds).</summary>
    public const float SprintSeconds = 6f, SprintGapSeconds = 1f;
    /// <summary>The buff's own length (data, 10 s; Musou's stim rules then scale a stim's lifetime, so it lasts <see cref="BuffLifetime"/> x <c>MusouCombatRules.StimLifetimeScale</c>), the regeneration multiplier while it runs (data) and the name of his equipment item.</summary>
    public const float BuffLifetime = 10f, BuffRegenMultiplier = 2f;
    public const string BuffName = "dread_buff";

    // ---- the entrance (data: evt_spawn_in, DN_NAV_Entrance) ----
    /// <summary>Every received damage is multiplied by 0 for this long after he spawns (data, 5 s) while the entrance clip of <see cref="EntranceSeconds"/> plays (data, 4.833 s).</summary>
    public const float InvulnerableSeconds = 5f, EntranceSeconds = 4.833f;

    /// <summary>His body as the sim holds it (<see cref="SimConfig.Bodies"/>): the data's speeds, the owner's health pool and the big capsule; the marine hit volumes (null: the config's).</summary>
    public static BodySpec Body() => new(BodyName, RunSpeed, StrafeSpeed, BackSpeed, SprintSpeed, WalkSpeed, AimRunSpeed, AimStrafeSpeed, AimBackSpeed,
        Health, RegenDelay, RegenRate, Shield, 0f, 0f) { CapsuleRadius = CapsuleRadius, CapsuleHeight = CapsuleHeight };

    /// <summary>Whether <paramref name="classIndex"/> is the Dreadnought in <paramref name="config"/>.</summary>
    public static bool IsDreadnought(SimConfig config, int classIndex) =>
        config.Extras.HasFlag(ContentExtras.Musou) && Loadouts.ClassOf(config, classIndex) is { } c && c.Name == ClassName;
}
