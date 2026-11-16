namespace SmSim.Extras.Warfront.Progression;

/// <summary>
/// Warfront's level-up cards (plan section 5; owner brief "XP, cards, and Ork scaling"). The numeric value is what <c>WarfrontPlayerState.Cards</c> stores (W4's list of ints) and what the wire carries; never renumber.
/// </summary>
public enum WarfrontCard
{
    None = 0,
    AttackSpeed = 1,
    MaxHp = 2,
    Damage = 3,
    CooldownReduction = 4,
    /// <summary>The tunable fifth stat type (owner: "+Max Armor as the recommended tuning default, not a locked design choice").</summary>
    MaxArmor = 5,
    /// <summary>+1 Retainer; only ever in slot 4 (slots 1 to 3 are stat cards).</summary>
    Retainer = 6,
}

/// <summary>Every number of the progression rules, in one place (all [ours/estimate] except where marked; spec "Added content: Warfront, progression").</summary>
public static class WarfrontProgressionRules
{
    /// <summary>The stat cards, in the fixed order the offer draws from.</summary>
    public static readonly WarfrontCard[] StatCards =
        [WarfrontCard.AttackSpeed, WarfrontCard.MaxHp, WarfrontCard.Damage, WarfrontCard.CooldownReduction, WarfrontCard.MaxArmor];

    public const int OfferSize = 4;
    /// <summary>Slot (0-based) that carries +1 Retainer while eligible.</summary>
    public const int RetainerSlot = 3;
    public const int RosterMax = 9;

    /// <summary>+10 % attack speed / fire rate per card.</summary>
    public const float AttackSpeedStep = 0.10f;
    /// <summary>+10 % max HP per card.</summary>
    public const float MaxHpStep = 0.10f;
    /// <summary>+10 % damage per card.</summary>
    public const float DamageStep = 0.10f;
    /// <summary>-8 % ability cooldown per card, never below <see cref="CooldownFloor"/> of the base.</summary>
    public const float CooldownStep = 0.08f;
    public const float CooldownFloor = 0.40f;
    /// <summary>+15 % max armour per card.</summary>
    public const float MaxArmorStep = 0.15f;
    /// <summary>A retainer gets this share of its player's stat-bonus magnitude (owner: one third).</summary>
    public const float RetainerShare = 1f / 3f;

    /// <summary>At least this many seconds between two picker openings in the field (the pick queues; aboard the ship there is no gap) [ours].</summary>
    public const double MinSecondsBetweenOpenings = 45.0;
    /// <summary>A lull: no damage dealt or taken for this long [ours].</summary>
    public const double LullQuietSeconds = 3.0;
    /// <summary>... and no Ork within this distance (m) [ours].</summary>
    public const float LullOrkDistance = 12f;
    /// <summary>The server takes card 1 after this many seconds so the protection cannot be camped [ours].</summary>
    public const double PickTimeoutSeconds = 20.0;

    /// <summary>Ork scaling (plan section 5): HP +8 % and damage +5 % per level above 1 of the highest player [ours/estimate]; +20 % max HP per additional human [owner, answer 4].</summary>
    public const float OrkHealthPerLevel = 0.08f;
    public const float OrkDamagePerLevel = 0.05f;
    public const float OrkHealthPerExtraHuman = 0.20f;
    /// <summary>The one knob for "hard" (owner answer 8): multiplies every newly spawned Ork's HP and damage; 1 = the table as written [ours].</summary>
    public const float OrkHealthBase = 1f;
    public const float OrkDamageBase = 1f;
}
