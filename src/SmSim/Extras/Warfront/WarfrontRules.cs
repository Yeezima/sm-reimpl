namespace SmSim.Extras.Warfront;

/// <summary>
/// Warfront (docs/added-content.md section 11): the persistent campaign on Musou Mode's battle. The one version of its rules, simulation and wire sections;
/// hashed into the config hash only under <see cref="ContentExtras.Warfront"/> (<c>SimConfig.ExtrasHash</c>), so raising it never touches another profile.
/// </summary>
public static class WarfrontRules
{
    /// <summary>6: the HQ defence (slice W13b: its battle rules, survive 8 minutes, the Battle section tag 47, the elite share by node, the HQ composed with the hub). 5: the stage flow (slice W5a: supervisor, stage children, the hub/stage/down/results wire, identity, leader-only deploy, resume, end of run, squad and co-op wiring). 4: the card picker and XP HUD wire (slice W8, tags 44 and 45); W9b's squad wiring, stats feed and strength ruling ride on the same number until the lead renumbers. 3: the progression takes effect in a node battle (slice W3b: XP, level-up refill, the picker and its protection, the stat cards, Ork scaling by level, the AttackSpeedBonus field, the squad and stuck-log seams, assists). 2: the node battle rules and hook seams (slice W3: D as a capture point, no Warboss, the fallen human's spectator time and wipe rule, +20 % Ork health per extra human). 1: the switch and the skeleton (slice W1).</summary>
    public const uint Version = 6;
}
