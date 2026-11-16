namespace SmContent.Extras.Musou;

/// <summary>
/// The Bomb Squig's model and rig in the install (expansion S2; docs/discovery.md "Exterminatus Ork roster"): the co-op set's <c>squig_bomb</c> on <c>havok_squig__hkxx</c> (blueprint
/// <c>ork_coop_squig_bomb</c>). Its clips (<c>smtool graph-dump --animations --archive data/chunks/ebp_sets__mp_coop_set.oppc --entry havok_squig__hkxx</c>): <c>SQG_NAV_RUN_F/L/R_01</c>,
/// <c>SQG_NAV_WALK_F/L/R_01</c>, <c>SQG_NAV_IDLE_01</c>, <c>SQG_NAV_TAUNT_01</c>, <c>SQG_NAV_WalkOnSpot_F_01</c>, <c>SQG_MEL_ANTIC_2_EXPLODE_01</c>. The picks are **ours**: the explode
/// anticipation for the lit fuse (the crowd's attack clip) and as its death (it bursts), the taunt for a stagger.
/// </summary>
public static class SquigRig
{
    public const string Archive = RigProfiles.OrkArchive, Entry = "havok_squig__hkxx", BodyVisual = "squig_bomb";

    public static readonly (string[] Names, string Contains) Run = (["SQG_NAV_RUN_F_01"], "RUN_F"),
        Idle = (["SQG_NAV_IDLE_01"], "IDLE"),
        Attack = (["SQG_MEL_ANTIC_2_EXPLODE_01"], "EXPLODE"),
        Stagger = (["SQG_NAV_TAUNT_01"], "TAUNT"),
        Death = (["SQG_MEL_ANTIC_2_EXPLODE_01"], "EXPLODE");

    /// <summary>The rig as a profile: only the data sits here; no marine or Ork graph map fits it.</summary>
    public static readonly RigProfile Profile = new("squig", Archive, Entry, BodyVisual);
}
