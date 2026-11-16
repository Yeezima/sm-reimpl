namespace SmSim.Extras.Warfront.Progression;

/// <summary>
/// The stat multipliers a player's (or a retainer's) cards add up to. Stacks add: n cards of a type give n times its step. A retainer gets one third of each bonus magnitude
/// (<see cref="WarfrontProgressionRules.RetainerShare"/>); it never has picks of its own. <see cref="Retainers"/> counts the +1 Retainer cards (the roster's business, not a stat).
/// <para>
/// Seams (verified 2026-11-17): the damage multiplier goes through <c>MusouSim.SetAttackerScale</c> (rewritten every tick by <c>World.MusouPowerupsStep</c>); the fire-rate and max-HP/armour
/// multipliers have no seam today (<c>FireBoostLeft</c>/<c>SimConfig.FireBoost</c> is a global factor that also spends no ammunition; the body's <c>HealthMax</c> comes from <c>Loadouts.Body</c>). See the W7 report.
/// </para>
/// </summary>
public readonly record struct WarfrontStatBonuses(
    float AttackSpeed, float MaxHp, float Damage, float CooldownScale, float MaxArmor, int Retainers)
{
    public static readonly WarfrontStatBonuses None = new(1f, 1f, 1f, 1f, 1f, 0);

    /// <summary>The bonuses for <paramref name="cards"/> (the saved list of ints; unknown values are ignored).</summary>
    public static WarfrontStatBonuses For(IEnumerable<int> cards) => Build(cards, 1f);

    /// <summary>A retainer's bonuses: one third of its player's magnitude (the cooldown floor applies after the share).</summary>
    public static WarfrontStatBonuses ForRetainer(IEnumerable<int> playerCards) => Build(playerCards, WarfrontProgressionRules.RetainerShare);

    static WarfrontStatBonuses Build(IEnumerable<int> cards, float share)
    {
        int speed = 0, hp = 0, dmg = 0, cd = 0, armor = 0, ret = 0;
        foreach (int c in cards)
            switch ((WarfrontCard)c)
            {
                case WarfrontCard.AttackSpeed: speed++; break;
                case WarfrontCard.MaxHp: hp++; break;
                case WarfrontCard.Damage: dmg++; break;
                case WarfrontCard.CooldownReduction: cd++; break;
                case WarfrontCard.MaxArmor: armor++; break;
                case WarfrontCard.Retainer: ret++; break;
            }
        return new(
            1f + share * speed * WarfrontProgressionRules.AttackSpeedStep,
            1f + share * hp * WarfrontProgressionRules.MaxHpStep,
            1f + share * dmg * WarfrontProgressionRules.DamageStep,
            MathF.Max(WarfrontProgressionRules.CooldownFloor, 1f - share * cd * WarfrontProgressionRules.CooldownStep),
            1f + share * armor * WarfrontProgressionRules.MaxArmorStep,
            ret);
    }

    /// <summary>
    /// The damage-taken scale that gives the same effective health as <see cref="MaxHp"/> without a max-HP seam (1 / multiplier). <c>DamageTakenScale</c> hits health and shield alike, so this also
    /// stretches the shield; a stop-gap the lead can wire through <c>ExtrasDamageTaken</c> until a body-level seam exists.
    /// </summary>
    public float EffectiveDamageTakenScale => 1f / MaxHp;

    /// <summary>
    /// The pure rule W3b calls from <c>ExtrasDamageTaken</c> (provisional approximation of max HP / max armour cards; bars stay unscaled): while the target's shield is above 0 a hit is scaled by
    /// 1 / the max-armour multiplier, otherwise by 1 / the max-HP multiplier. Multiply into the scale already there.
    /// </summary>
    public static float DamageTaken(float shield, in WarfrontStatBonuses b) => shield > 0f ? 1f / b.MaxArmor : 1f / b.MaxHp;
}
