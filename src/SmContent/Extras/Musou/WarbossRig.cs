namespace SmContent.Extras.Musou;

/// <summary>
/// Musou Mode's Warboss on his own rig (owner, 2026-11-06: "the final boss should use a warboss model"): the campaign's <c>warboss_regular</c> body and <c>warboss_head_regular</c> head on
/// <c>havok_warboss_regular__hkxx</c>, read from the campaign set <c>ebp_sets__sequence2_core_units</c> of the player's install (a singleplayer dependency, docs/discovery.md "Warboss model").
/// The rig, meshes and clips are original assets; which clip plays for the crowd's run, idle, attack, stagger and death is **ours** (picked by name; the graphs are not read).
/// </summary>
public static class WarbossRig
{
    public const string Archive = "data/chunks/ebp_sets__sequence2_core_units.oppc", Entry = "havok_warboss_regular__hkxx", BodyVisual = "warboss_regular", Head = "warboss_head_regular";

    /// <summary>
    /// The clips by the crowd's five jobs: candidates in order, then a part of a name to fall back on. The rig's 48 clips (<c>smtool graph-dump --animations --archive
    /// data/chunks/ebp_sets__sequence2_core_units.oppc --entry havok_warboss_regular__hkxx</c>) have walks but no run, no standing idle and no death of the usual names (his campaign fight is
    /// scripted), so the picks are **ours**: the forward walk for the run (played at the crowd's rate), the floor idle, the shove for a strike (the slam and charge too: the crowd has one attack
    /// clip), the charge's wall-hit exit for a stagger (he never staggers) and the explosion reaction for the death.
    /// </summary>
    public static readonly (string[] Names, string Contains) Run = (["WB_Nav_Walk_F"], "Walk_F"),
        Idle = (["WB_Nav_Floor_Idle", "WB_Nav_Death_Idle"], "Idle"),
        Attack = (["WB_Att_Shove", "WB_Att_Ground_Pound"], "_Att_"),
        Stagger = (["WB_Att_Charge_wall_hit_exit", "WB_Get_Up"], "_REC_"),
        Death = (["WB_REC_Explode"], "Death");

    /// <summary>The rig as a profile: only the data sits here; no marine or Ork graph map fits it, so nothing may read those through this profile.</summary>
    public static readonly RigProfile Profile = new("warboss", Archive, Entry, BodyVisual);
}
