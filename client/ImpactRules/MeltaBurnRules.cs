using System;

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
    /// The burn starts on the hit event itself: a melta weapon, a body hit (not the world), another player, and not within the gap of the last set on him.
    /// Death, ragdoll or corpse state is not an input: a hit that kills starts it too, in the same frame.
    /// </summary>
    public static bool Due(bool meltaWeapon, bool bodyHit, int victim, int ownId, double now, double lastAt) =>
        meltaWeapon && bodyHit && victim > 0 && victim != ownId && now - lastAt >= Gap;
}
