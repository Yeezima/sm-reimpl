using SmSim.Extras.Warfront.Squad;

namespace SmSim.Extras.Warfront.Retainers;

/// <summary>
/// The rules of Warfront's retainers as real World players (Phase B, T2 "the retainer spine"; decisions 2026-11-23 series 36 and 2026-11-24 series 37; the owner's answers of 2026-11-25).
/// A retainer is a World player in a puppet id with <see cref="SmSim.Extras.Musou.Campaign.MarineRole.Squad"/>: it draws, animates, sounds and fights through the normal player path, so every class and
/// weapon looks and sounds as a human's (owner 2026-11-24: "EXACTLY the player animations, visual effects and audio"). Spec "Added content: Warfront: retainers as World players".
/// Each switch below is the single place of one owner answer, so a changed answer is a one-line change.
/// </summary>
public static class WarfrontRetainerRules
{
    /// <summary>
    /// Owner 2026-11-25 (a), "Base player damage only": a retainer deals full BASE player damage (the squad's 0.40 retired) but none of the war's human-only scales (the x1.2 ranged damage, no range
    /// fall-off, a pierce that loses nothing, the x0.6 cleave trim): <see cref="SmSim.Extras.Musou.Campaign.TfwCombatRules.ScalesPlayer"/> stays false for it. True gives a retainer those scales as a human's.
    /// The level curve and the 1/3 card share apply either way. Kind: owner; confidence: stated.
    /// </summary>
    public const bool HumanOnlyScales = false;

    /// <summary>Owner 2026-11-25 (b): a retainer's melee stuns a boss as a human's does (an AI Marine's never does). False restores the AI Marines' rule. Kind: owner (our reading of "retainers ARE stunned by bosses").</summary>
    public const bool StunsBosses = true;

    /// <summary>Owner 2026-11-25 (b): a scrap trap hurts a retainer as it hurts a human (a share of his full health and armour), not the AI Marines' outright kill. False restores the outright kill. Kind: owner; confidence: stated.</summary>
    public const bool TrapsHurtLikeHumans = true;

    /// <summary>Owner 2026-11-25 (b): a retainer takes no pickup (the puppets' exclusion in Musou's drop code stays as it is; nothing here changes it). Recorded for the spec only.</summary>
    public const bool TakesPickups = false;

    /// <summary>The share of every hit a retainer takes (the squad's rule, <see cref="SquadRules.DamageTaken"/>), in place of the puppets' 0.10; the cards' and the level's health share on top (lead decision 2026-11-25, "keep their damage-taken reduction").</summary>
    public const float DamageTaken = SquadRules.DamageTaken;

    /// <summary>The share of an Ork's level damage multiplier a retainer takes (<see cref="SquadRules.OrkLevelDamageShare"/>: none).</summary>
    public const float OrkLevelDamageShare = SquadRules.OrkLevelDamageShare;

    /// <summary>A downed retainer can be revived by anyone (a human or another retainer holding Use within <see cref="SquadRules.PlayerReviveReach"/> for <see cref="SquadRules.PlayerReviveSeconds"/>) for this long; then it is out for the battle [owner].</summary>
    public const float DownedSeconds = SquadRules.DownedSeconds;

    /// <summary>Landing ring round the owner: Marine k lands at this radius plus k x step (the squad's numbers, <see cref="SquadRules.LandRadius"/>), snapped to the walking graph.</summary>
    public const float LandRadius = SquadRules.LandRadius, LandRadiusStep = SquadRules.LandRadiusStep;

    /// <summary>A retainer that hit an Ork shares its kill as an assist when the kill follows within this (the squad's rule).</summary>
    public const float AssistSeconds = SquadRules.AssistSeconds;
}
