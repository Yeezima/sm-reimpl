namespace SmSim.Extras.Warfront;

/// <summary>
/// Warfront (docs/added-content.md section 11): the persistent campaign on Musou Mode's battle. The one version of its rules, simulation and wire sections;
/// hashed into the config hash only under <see cref="ContentExtras.Warfront"/> (<c>SimConfig.ExtrasHash</c>), so raising it never touches another profile.
/// </summary>
public static class WarfrontRules
{
    /// <summary>4: the squad wiring and the stats feed (slice W9b: the squad sim on the battle seams, squad-kill XP, the retainer third, the ledger events) and the squad strength ruling of 2026-11-17 (damage dealt 0.40, damage taken 0.35, no health regeneration in a battle, one path search a tick, the stranded failsafe). 3: the progression takes effect in a node battle (slice W3b: XP, level-up refill, the picker and its protection, the stat cards, Ork scaling by level, the AttackSpeedBonus field, the squad and stuck-log seams, assists). 2: the node battle rules and hook seams (slice W3: D as a capture point, no Warboss, the fallen human's spectator time and wipe rule, +20 % Ork health per extra human). 1: the switch and the skeleton (slice W1).</summary>
    public const uint Version = 4;
}
