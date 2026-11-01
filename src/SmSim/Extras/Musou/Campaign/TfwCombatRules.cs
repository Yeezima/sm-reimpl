namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The owner's melee and firepower changes for Musou Mode (pass 2, M5; items 7, 21, 22, 24): the shove and the fall, the three big frags, the 20-enemy cleave, the piercing shots. They are set on
/// <see cref="MusouSim.War"/> by <see cref="World.StartTfw"/> alone, so the 5-wave test aid and every other mode never read them (<c>MusouSkirmishPinTests</c>, <c>PvpCombatIsolationTests</c>). Nothing
/// here changes a shared default: the core's frag, melee and shot rules stay as they are, and the Musou Mode ones sit on top through the Musou hooks. Every value is ours (kind: ours, spec "Added
/// content: Musou Mode: melee and firepower"). Distances in metres, times in seconds. Hashed only through <see cref="TfwRules.Version"/>.
/// </summary>
public sealed record TfwCombatRules
{
    public static TfwCombatRules Default { get; } = new();

    // ---- the cleave (item 22)

    /// <summary>Most enemies one swing strikes, nearest first (the owner: "up to 20").</summary>
    public int CleaveTargets { get; init; } = 20;

    // ---- the fall (item 7; design section C)

    /// <summary>A fodder Ork or Guardsman lies down this long, then takes <see cref="GetUpSeconds"/> to stand; he neither moves nor attacks in that time (design: 1.8 and 0.7).</summary>
    public float DownSeconds { get; init; } = 1.8f;
    public float GetUpSeconds { get; init; } = 0.7f;
    public float KnockedSeconds => DownSeconds + GetUpSeconds;
    /// <summary>The time in a fall's clip (of <paramref name="clip"/> seconds) after <paramref name="seconds"/> on the ground: it falls in at most 0.8 s, lies at the end, and plays backwards over <see cref="GetUpSeconds"/>.</summary>
    public float FallClipTime(float seconds, float clip)
    {
        float rising = KnockedSeconds - seconds;
        if (rising < GetUpSeconds) return clip * MathF.Max(0f, rising) / GetUpSeconds;
        return MathF.Min(clip, seconds * MathF.Max(1f, clip / 0.8f));
    }
    /// <summary>How far each contact throws him (design: 1.5, 3, 4, 4; a melee hit is ours, the core's heavy knockback).</summary>
    public float WalkPush { get; init; } = 1.5f;
    public float SprintPush { get; init; } = 3f;
    public float ChargePush { get; init; } = 4f;
    public float LandingPush { get; init; } = 4f;
    public float MeleePush { get; init; } = 2f;
    /// <summary>The chance each contact kills outright (design: 3 %, 10 %, 25 %; landing as a charge, a melee hit as a sprint: ours).</summary>
    public float WalkDeath { get; init; } = 0.03f;
    public float SprintDeath { get; init; } = 0.10f;
    public float ChargeDeath { get; init; } = 0.25f;
    public float LandingDeath { get; init; } = 0.25f;
    public float MeleeDeath { get; init; } = 0.10f;
    /// <summary>A Marine counts as moving from this speed, flat, in m/s (under it nobody is shoved: standing still is no shove).</summary>
    public float MoveSpeed { get; init; } = 1.5f;
    /// <summary>Metres past the ring <see cref="MusouRules.PlayerRing"/> keeps enemies at that count as contact, in the front half of the move.</summary>
    public float ContactMargin { get; init; } = 0.25f;
    /// <summary>A landing shoves all round, this far, after a fall of at least <see cref="LandingFall"/> m/s (a step off a kerb is no landing).</summary>
    public float LandingReach { get; init; } = 2.5f;
    public float LandingFall { get; init; } = 4f;
    /// <summary>Most bodies one Marine tests a tick (design: 16).</summary>
    public int ShoveTests { get; init; } = 16;

    // ---- the frag (item 21)

    /// <summary>Frags per throw: the player's one and this many less one beside it; each is one fan <see cref="FragSpreadDegrees"/> from the next.</summary>
    public int FragsPerThrow { get; init; } = 3;
    public float FragSpreadDegrees { get; init; } = 24f;
    /// <summary>A Musou Mode frag's blast radius and its fall-off's distances, times this (the data's 10 m to 20 m); the damage stays the data's.</summary>
    public float FragRadiusScale { get; init; } = 2f;

    // ---- piercing (item 24)

    /// <summary>A shot that has gone through an enemy keeps this share of its damage for the next.</summary>
    public float PierceKeep { get; init; } = 0.85f;
    /// <summary>The most enemies any shot goes through; a stack buffer's size.</summary>
    public const int MostPierced = 16;
    /// <summary>Most enemies one cone shot covers (the 5-wave run's 24 for the melta: a cone goes through what it covers).</summary>
    public int ConeTargets { get; init; } = 40;

    /// <summary>
    /// How many enemies a shot of <paramref name="weapon"/> goes through before the one that stops it (the owner: heavier weapons pierce more): the lascannon 8, the heavy bolter and the plasma cannon
    /// 5, the stalker bolter 4, the storm bolter and the melta 3, a pistol 1, any other ranged weapon 2. A projectile that bursts bursts on the one after them.
    /// </summary>
    public static int Pierced(WeaponSpec weapon)
    {
        string name = weapon.Name;
        bool Has(string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);
        if (Has("las_cannon")) return 8;
        if (Has("heavy_bolter") || Has("plasma_cannon") || Has("auto_cannon")) return 5;
        if (Has("sniper_rifle")) return 4;
        if (Has("storm_bolter") || Has("melta")) return 3;
        return weapon.Slot == WeaponSlot.Pistol ? 1 : 2;
    }

    /// <summary>Whether a thrown item is a frag (a blind grenade, a melta bomb or stims are not).</summary>
    public static bool IsFrag(ThrownSpec item) => item.Stim is null && !item.IsBlindGrenade && item.Name.Contains("frag", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A frag's projectile with the bigger blast: the radius and the fall-off's distances scaled. The thrower is left out of it: at twice the data's reach a frag at arm's length would otherwise
    /// kill him with his own three.
    /// </summary>
    public ProjectileSpec BiggerFrag(ProjectileSpec spec) => spec with { Splash = spec.Splash is { } s ? Bigger(s) : null, ExtraSplashes = [.. spec.ExtraSplashes.Select(Bigger)] };

    SplashSpec Bigger(SplashSpec s) => s with
    {
        Radius = s.Radius * FragRadiusScale, IgnoreSelf = true,
        FallOff = [.. s.FallOff.Select(p => p with { Distance = p.Distance * FragRadiusScale })],
    };
}
