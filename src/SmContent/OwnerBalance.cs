using SmSim;

namespace SmContent;

/// <summary>
/// One weapon's damage per second at a distance (body hits; <see cref="OwnerBalance"/>). <see cref="Burst"/> / <see cref="Sustained"/>: on the
/// health scale (damage x fall-off x body multiplier), while the trigger is held (per effective cooldown; a burst weapon fires
/// <see cref="Rounds"/> per pull) and over whole clips with their reloads. <see cref="BurstVsMarine"/> / <see cref="SustainedVsMarine"/>: a
/// full marine's shield plus health divided by the time to kill it (shield first at the weapon's shield multiplier, the rest on health at the
/// body multiplier, as <c>Combat.ApplyDamage</c>; a shield multiplier of 0 passes the shield by, so only health has to go); with no shield the
/// two pairs are equal.
/// </summary>
public sealed record WeaponDps(string Weapon, string Variant, float Damage, float Cooldown, int Clip, float Reload, float FallOff,
    float BodyMultiplier, float ShieldBodyMultiplier, float Burst, float Sustained, float BurstVsMarine, float SustainedVsMarine)
{
    /// <summary>Rounds one trigger pull fires (<see cref="WeaponSpec.BurstLength"/>; 1 for a weapon without bursts).</summary>
    public int Rounds { get; init; } = 1;
}

/// <summary>One owner's-decision damage change: the weapon (or one of its perk variants) as the data give it and as used, its reference and the target ratio.</summary>
/// <param name="Ratio">Target DPS as a fraction of the reference's (1: match; 1.25: 25 % above).</param>
/// <param name="Data">The weapon or variant as read (the data's damage).</param>
/// <param name="Result">As used (equal to <paramref name="Data"/> when nothing changed).</param>
/// <param name="Reference">The reference weapon (its default entry, no perk).</param>
/// <param name="Variant">"default", or the variant's upgrade strings joined with "+".</param>
public sealed record OwnerBalanceRow(float Ratio, WeaponSpec Data, WeaponSpec Result, WeaponSpec Reference, string Decision, string Variant = "default")
{
    public bool Changed => Result.Damage != Data.Damage;
}

/// <summary>
/// <b>Owner's balance decisions (2026-10-28): deliberate changes from the original</b> (docs/changes-from-original.md, spec/behavior.md
/// "Owner's bolter balance"). The one place where the data's weapon values are overridden for the owner; the data stay readable
/// (<c>smtool perks</c>; <c>smtool weapon-dps</c> prints data and result) and every changed value is a row of <see cref="Apply"/>.
/// <list type="bullet">
/// <item>"buff the standard bolter as well to match the dps of the bolt pistol": the primary-slot Bolter's damage per shot is raised, when
/// needed, so that its body-shot DPS at <see cref="MediumRange"/>, while the trigger is held and sustained over clip and reload, is at least
/// the same race's bolt pistol's (<see cref="Rules"/>, ratio 1). A bolter that already meets both is left as it is.</item>
/// <item>"buff that true grit bolter to be moderately more dps than bolt pistol. it should be powerful to use": the pistol-slot Bolter
/// (True Grit) gets the damage per shot that puts both DPS figures at least <see cref="TrueGritRatio"/> times the bolt pistol's (ours: 25 %
/// for "moderately more"; low confidence, for the playtest).</item>
/// </list>
/// The DPS compared is against a full marine (<see cref="WeaponDps.BurstVsMarine"/>, <see cref="WeaponDps.SustainedVsMarine"/>: shield and
/// health of the config; ours, pending the owner): on the health scale alone the data's bolter already out-damages the bolt pistol (+10 % burst,
/// +6 % sustained), but its shield multiplier (0.505 against the pistol's 0.7) makes it the slower killer of a shielded marine (−10 % / −13 %),
/// which is what the owner's "buff ... to match" describes. Cooldowns are rounded up to whole ticks as the simulation fires them
/// (<see cref="OrkBalance.EffectiveCooldown"/>), sustained = clip x damage / (pulls x cooldown + reload).
/// The new damage is the smallest that meets both targets, rounded up to 0.1 (so the binding figure lands on its target, the other above it).
/// Each perk variant of the weapon (Kraken Bolts) is measured against the same target on its own (the default bolt pistol's DPS times the
/// rule's ratio) and gets its own damage; a variant already at its target keeps the data's.
/// </summary>
public static class OwnerBalance
{
    /// <summary>The distance the DPS targets are measured at: "medium range", 15 m (the maintainer's brief; ours).</summary>
    public const float MediumRange = 15f;

    /// <summary>The True Grit bolter's DPS over the bolt pistol's: 1.25 (ours, for the owner's "moderately more"; low confidence).</summary>
    public const float TrueGritRatio = 1.25f;

    /// <summary>The standard bolter's DPS over the bolt pistol's: 1 ("match the dps of the bolt pistol").</summary>
    public const float BolterRatio = 1f;

    /// <summary>One override rule: the weapon (wargear name), the reference weapon and the target ratio.</summary>
    public sealed record Rule(string Weapon, string Reference, float Ratio, string Decision);

    const string StandardDecision = "owner 2026-10-28: the standard bolter matches the bolt pistol's DPS";
    const string TrueGritDecision = "owner 2026-10-28: the True Grit bolter moderately more DPS than the bolt pistol (+25 %: ours)";

    /// <summary>The overrides, both races and the gold (Relic) twins, which the data give the same numbers.</summary>
    public static readonly IReadOnlyList<Rule> Rules =
    [
        new("sm_mp_ranged_bolter", "sm_mp_pistol_bolt_pistol", BolterRatio, StandardDecision),
        new("sm_mp_ranged_bolter_gold", "sm_mp_pistol_bolt_pistol", BolterRatio, StandardDecision),
        new("csm_mp_ranged_bolter", "csm_mp_pistol_bolt_pistol", BolterRatio, StandardDecision),
        new("csm_mp_ranged_bolter_gold", "csm_mp_pistol_bolt_pistol", BolterRatio, StandardDecision),
        new("sm_mp_pistol_bolter", "sm_mp_pistol_bolt_pistol", TrueGritRatio, TrueGritDecision),
        new("sm_mp_pistol_bolter_gold", "sm_mp_pistol_bolt_pistol", TrueGritRatio, TrueGritDecision),
        new("csm_mp_pistol_bolter", "csm_mp_pistol_bolt_pistol", TrueGritRatio, TrueGritDecision),
        new("csm_mp_pistol_bolter_gold", "csm_mp_pistol_bolt_pistol", TrueGritRatio, TrueGritDecision),
    ];

    /// <summary>The weapon's DPS figures at <paramref name="distance"/> against a target with <paramref name="shield"/> and <paramref name="health"/>.</summary>
    public static WeaponDps Measure(WeaponSpec w, float distance, float tickSeconds, float shield, float health, string variant = "default")
    {
        float fall = Combat.FallOff(distance, w);
        float raw = w.Damage * fall;
        float cooldown = OrkBalance.EffectiveCooldown(w.ShotCooldown, tickSeconds);
        int rounds = w.Bursts ? w.BurstLength : 1;   // a burst's later rounds share the first's cooldown (Combat.StepPlayer)
        float burstRaw = cooldown > 0f ? rounds * raw / cooldown : 0f;
        int ammo = Math.Max(1, w.AmmoPerShot);   // sim/stormbolter-ammo: the storm bolter spends 2 rounds a shot
        int shots = (w.ClipSize + ammo - 1) / ammo;
        int pulls = (shots + rounds - 1) / rounds;
        float clipTime = pulls * cooldown + w.ReloadTime;
        float sustainedRaw = clipTime > 0f ? shots * raw / clipTime : 0f;
        // Against a full marine: raw damage to kill = shield / shield multiplier + health / body multiplier (Combat.ApplyDamage; a shield
        // multiplier of 0 never touches the shield, so only health counts).
        float needed = (w.ShieldBodyMultiplier > 0f && shield > 0f ? shield / w.ShieldBodyMultiplier : 0f)
            + (w.BodyMultiplier > 0f ? health / w.BodyMultiplier : float.PositiveInfinity);
        float pool = MathF.Max(0f, shield) + health;
        float VsMarine(float rawRate) => float.IsFinite(needed) && needed > 0f ? rawRate * pool / needed : 0f;
        return new WeaponDps(w.Name, variant, w.Damage, cooldown, w.ClipSize, w.ReloadTime, fall, w.BodyMultiplier, w.ShieldBodyMultiplier,
            burstRaw * w.BodyMultiplier, sustainedRaw * w.BodyMultiplier, VsMarine(burstRaw), VsMarine(sustainedRaw)) { Rounds = rounds };
    }

    /// <summary>The variant label of a perk variant ("default" for the base).</summary>
    public static string VariantName(UpgradeVariant<WeaponSpec> v) => string.Join("+", v.Upgrades);

    /// <summary>
    /// The smallest damage (rounded up to 0.1) that brings <paramref name="w"/>'s burst and sustained DPS against a full marine to at least
    /// <paramref name="target"/>'s times <paramref name="ratio"/>; the weapon's own damage when it already meets both (never lowered).
    /// </summary>
    public static float DamageFor(WeaponSpec w, WeaponDps target, float ratio, float tickSeconds, float shield, float health)
    {
        var before = Measure(w, MediumRange, tickSeconds, shield, health);
        // Damage scales both figures linearly: the factor that brings the lower one to its target.
        float need = MathF.Max(before.BurstVsMarine > 0f ? ratio * target.BurstVsMarine / before.BurstVsMarine : 1f,
            before.SustainedVsMarine > 0f ? ratio * target.SustainedVsMarine / before.SustainedVsMarine : 1f);
        return need > 1f + 1e-6f ? MathF.Ceiling(w.Damage * need * 10f - 1e-3f) / 10f : w.Damage;
    }

    /// <summary>
    /// The weapons with the owner's overrides applied (<see cref="Rules"/>; a rule whose weapon or reference is missing, or whose weapon is
    /// not hit-scan, does nothing) and one row per weapon and per perk variant a rule reached. The targets are met against a full marine of
    /// <paramref name="shield"/> and <paramref name="health"/> (the config's), burst and sustained, at <see cref="MediumRange"/>; the reference is
    /// always the data's default entry.
    /// </summary>
    public static (IReadOnlyList<WeaponSpec> Weapons, IReadOnlyList<OwnerBalanceRow> Rows) Apply(IReadOnlyList<WeaponSpec> weapons, float tickSeconds,
        float shield, float health, IReadOnlyList<Rule>? rules = null)
    {
        rules ??= Rules;
        var result = weapons.ToList();
        var rows = new List<OwnerBalanceRow>();
        int IndexOf(string name) => result.FindIndex(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        foreach (var rule in rules)
        {
            int i = IndexOf(rule.Weapon), r = IndexOf(rule.Reference);
            if (i < 0 || r < 0) continue;
            var w = result[i];
            var reference = weapons[r];   // the data's reference (a rule never chains on another's result)
            if (w.Delivery != DeliveryKind.Raycast || !(w.Damage > 0f)) continue;
            var target = Measure(reference, MediumRange, tickSeconds, shield, health);
            float damage = DamageFor(w, target, rule.Ratio, tickSeconds, shield, health);
            var variants = new List<UpgradeVariant<WeaponSpec>>(w.Variants.Count);
            bool variantChanged = false;
            foreach (var v in w.Variants)
            {
                var spec = v.Spec;
                if (spec.Delivery == DeliveryKind.Raycast && spec.Damage > 0f)
                {
                    float vd = DamageFor(spec, target, rule.Ratio, tickSeconds, shield, health);
                    if (vd != spec.Damage) { spec = spec with { Damage = vd }; variantChanged = true; }
                }
                variants.Add(spec == v.Spec ? v : v with { Spec = spec });
                rows.Add(new OwnerBalanceRow(rule.Ratio, v.Spec, spec, reference, rule.Decision, VariantName(v)));
            }
            var changed = damage == w.Damage && !variantChanged ? w : w with { Damage = damage, Variants = variantChanged ? variants : w.Variants };
            result[i] = changed;
            rows.Insert(rows.Count - w.Variants.Count, new OwnerBalanceRow(rule.Ratio, w, changed, reference, rule.Decision));
        }
        return (result, rows);
    }

    /// <summary>The owner's grenade decision (2026-10-29, verbatim): "grenades shouldn't bounce so far when thrown".</summary>
    public const string GrenadeBounceDecision = "owner 2026-10-29: grenades shouldn't bounce so far when thrown";

    /// <summary>
    /// fix/grenades (<b>deliberate change</b>, the owner's request; the rule is ours): every thrown item with a rigid body and a usable Havok body material
    /// (frag and blind grenades of both races, the Ork's stikkbomb; their perk variants too) bounces off the ground with <see cref="RigidSpec.GroundMaterial"/>:
    /// the blueprint's own restitution (0.65) and friction (2), which the data's rule leaves unused, take speed off before the data's kept fractions
    /// (0.3 across, 0.4 up). Steep landings then stop where they land (hop straight up), shallow ones skid about a metre; the ground fuse (1 s, the
    /// stikkbomb 2 s), the flight and the walls are unchanged. An item without a usable material keeps the data's rule (noted).
    /// </summary>
    public static IReadOnlyList<ThrownSpec> WithGrenadeBounce(IReadOnlyList<ThrownSpec> thrown, List<string> notes)
    {
        ThrownSpec One(ThrownSpec t)
        {
            if (t.Projectile.Rigid is not { } rigid) return t;
            if (!rigid.HasBodyMaterial) { notes.Add($"grenade bounce (owner): '{t.Name}' has no usable body material; the data's bounce stays"); return t; }
            return t with { Projectile = t.Projectile with { Rigid = rigid with { GroundMaterial = true } } };
        }
        return thrown.Select(t => One(t) with { Variants = t.Variants.Select(v => v with { Spec = One(v.Spec) }).ToArray() }).ToArray();
    }
}
