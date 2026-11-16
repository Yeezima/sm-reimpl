using System;
using System.Collections.Generic;
using SmSim;

namespace SmClient.ImpactRules;

/// <summary>fix/melta-burn-onhit: when and where a melta hit burns its victim (the view plays it: NetworkView.MeltaBurn.cs). Bone list, gap and rule are ours.</summary>
public static class MeltaBurnRules
{
    /// <summary>Head to feet, both arms and legs (markers as in the data's <c>actn_melta_death</c>, plus arms and feet; a name the rig lacks is skipped).</summary>
    public static readonly string[] Bones =
    [
        "rig_head", "rig_chest", "rig_spine1", "rig_pelvis", "rig_lupperarm", "rig_rupperarm", "rig_lforearm", "rig_rforearm", "rig_lhand", "rig_rhand",
        "rig_lthigh", "rig_rthigh", "rig_lcalf", "rig_rcalf", "rig_lfoot", "rig_rfoot",
    ];

    /// <summary>At most one set per victim in this many seconds.</summary>
    public const double Gap = 0.25;

    public static bool IsMelta(string wargear) => wargear.Contains("melta", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// claude/hit-fx-answers (owner, 2026-11-11: the melta burns the body "of targets it kills"): whether a kill feed entry is a melta weapon's kill. Only <see cref="KillCause.Weapon"/> counts: the
    /// melta bomb is a thrown item (<see cref="KillCause.Thrown"/>, index into <see cref="SimConfig.Thrown"/>) and never reaches this test; a melee, jump-pack or perk kill does not either.
    /// </summary>
    public static bool KillBurns(KillCause cause, int index, IReadOnlyList<string> weaponNames) =>
        cause == KillCause.Weapon && index >= 0 && index < weaponNames.Count && IsMelta(weaponNames[index]);

    /// <summary>Musou (the crowd's deaths carry no weapon, only the killer): whether the killer's weapon in hand (null: unknown or none) is a melta, for a death by a shot.</summary>
    public static bool CrowdKillBurns(bool shot, string? killerInHand) => shot && killerInHand is not null && IsMelta(killerInHand);

    /// <summary>The crowd's burn (Musou): heights above the body's feet of the few effects that stand for the whole-body set (a crowd body has no skeleton to follow; ours).</summary>
    public static readonly float[] CrowdHeights = [0.25f, 0.7f, 1.1f, 1.5f];

    /// <summary>The crowd's burn costs: sets per frame, and the farthest body that gets one (m, ours; the rest of the crowd is too far to read it).</summary>
    public const int CrowdSetsPerFrame = 3;
    public const float CrowdMetres = 45f;

    /// <summary>
    /// The burn starts on the hit event itself: a melta weapon, a body hit (not the world), another player, and not within the gap of the last set on him.
    /// Death, ragdoll or corpse state is not an input: a hit that kills starts it too, in the same frame.
    /// </summary>
    public static bool Due(bool meltaWeapon, bool bodyHit, int victim, int ownId, double now, double lastAt) =>
        meltaWeapon && bodyHit && victim > 0 && victim != ownId && now - lastAt >= Gap;
}
