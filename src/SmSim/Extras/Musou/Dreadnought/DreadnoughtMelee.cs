
namespace SmSim.Extras.Musou;

/// <summary>
/// The Musou Dreadnought's arm (D5; docs/musou-dreadnought-design.md sections 3.3, 3.4): the five strikes of the install's <c>dread_melee_default</c> as the core's melee steps. Every number is the data's
/// (<c>smtool dreadnought-dump</c>; kind: data, confidence Verified-local): damages, shield damages, reach 4 m, the clips' lengths and the sequencer tags (hit <c>ACTN_Hit_Targets_Multiplayer</c>, <c>BRANCH_MeleeCombo</c>,
/// <c>BRANCH_MeleeEnd</c>, <c>BRANCH_MeleeEndMoving</c>, <c>ACTN_Pound_Hit</c>, <c>ACTN_Stomp_Hit</c>) in seconds from the strike's start. The mapping onto the core's strike lists is ours (provisional approximation):
/// light X (Melee) = X01, X02 (the press after X02 goes back to X01: the core's wrap), heavy Y = the stomp from rest, Y01 after an X, and the stomp again after Y01 (the graph: X then Y is Y01, Y then Y the stomp,
/// Y from rest the stomp); the rush (Sprint + Melee) is the marine bull rush's path and clip timing with the data's 500 and 125 and the same reach (the 4.2 s <c>DN_MEL_Bullrush_Attack</c> root motion is not read).
/// Musou's own melee scales (attack speed, cleave, heals) apply on top as to every class (owner, 2026-11-14: "Musou scales"). Added content, Musou only.
/// </summary>
public static class DreadnoughtMelee
{
    /// <summary>The slam of the Y01 strike (<c>actn_pound_hit</c>: 500, stun damage 1000) and the stomp's (<c>actn_stomp_hit</c>: 50) and a footfall's (<c>evt_*_stomp</c>: 25): a sphere of 10 m each (data: <c>area_of_effect/shape</c>).</summary>
    public const float AreaRadius = 10f, SlamDamage = 500f, StompDamage = 50f, FootfallDamage = 25f;

    /// <summary>Reach (metres) of the arm's strikes (<c>max_range</c>, data).</summary>
    public const float Reach = 4f;

    static SplashSpec Area(float damage, float armour) => new(AreaRadius, new DamageSpec(damage, armour, 1f, 1f, 1f, 1f, false) { Type = DamageType.Melee }, [], false, true, "target_all");

    /// <summary>The slam's, the stomp's and a footfall's blast.</summary>
    public static SplashSpec Slam => Area(SlamDamage, 0f);
    public static SplashSpec Stomp => Area(StompDamage, 250f);
    public static SplashSpec Footfall => Area(FootfallDamage, 25f);

    /// <summary>
    /// <paramref name="step"/> run <paramref name="speed"/> times as fast (owner, 2026-11-15: "melee attacks are 30% faster"): every time in it (seconds from the strike's start) divided by the speed, so the hit, the windows and the end
    /// stay in the same places of the clip when the clip is played at that rate. Damages, reach and flags are not touched; a time that is infinity (no window) stays so.
    /// </summary>
    public static MeleeStep Faster(MeleeStep step, float speed)
    {
        static float T(float t, float k) => float.IsFinite(t) ? t / k : t;
        return step with
        {
            Duration = step.Duration / speed, HitTime = step.HitTime / speed, ComboOpen = step.ComboOpen / speed, ComboClose = step.ComboClose / speed,
            RangedAt = step.RangedAt / speed, MoveAt = step.MoveAt / speed, YComboOpen = step.YComboOpen / speed, YComboClose = step.YComboClose / speed,
            NoReactFrom = step.NoReactFrom / speed, NoReactTo = step.NoReactTo / speed, EvadeAt = T(step.EvadeAt, speed), StringResetAt = T(step.StringResetAt, speed),
        };
    }

    static IReadOnlyList<MeleeStep> Faster(IReadOnlyList<MeleeStep> steps) => [.. steps.Select(s => Faster(s, DreadnoughtRules.MeleeSpeed))];

    /// <summary>X01 and X02 (light): the data's tags (100 / 50 and 125 / 75, shield 250 / 100) at <see cref="DreadnoughtRules.MeleeSpeed"/>.</summary>
    public static IReadOnlyList<MeleeStep> Light() => Faster(DataLight());

    /// <summary>The stomp (Y from rest, 50 in 10 m) and Y01 (50 and the 500 slam), the data's tags at <see cref="DreadnoughtRules.MeleeSpeed"/>.</summary>
    public static IReadOnlyList<MeleeStep> Heavy() => Faster(DataHeavy());

    /// <summary>X01 and X02 as the data times them (seconds from the strike's start; kind: data).</summary>
    public static IReadOnlyList<MeleeStep> DataLight() =>
    [
        new MeleeStep(2.397f, 0.867f, 0.967f, 2.2f, 1.2f, 1.2f, 100f, 50f, 0f) { ShieldDamage = 250f, SecondaryShieldDamage = 100f, HeavyNext = 2 },
        new MeleeStep(2.116f, 0.600f, 1.000f, 1.9f, 1.3f, 1.167f, 125f, 75f, 0f) { ShieldDamage = 250f, SecondaryShieldDamage = 100f, HeavyNext = 2 },
    ];

    /// <summary>The stomp and Y01 as the data times them.</summary>
    public static IReadOnlyList<MeleeStep> DataHeavy() =>
    [
        new MeleeStep(3.578f, 1.200f, 1.333f, 3.4f, 2.0f, 2.0f, 0f, 0f, 0f) { Stomp = Stomp, InPlace = true },
        new MeleeStep(3.578f, 1.200f, 2.000f, 3.4f, 1.933f, 2.033f, 50f, 0f, 0f) { ShieldDamage = 200f, Stomp = Slam, InPlace = true, YNext = 1, YComboOpen = 2.0f, YComboClose = 3.4f },
    ];
}
