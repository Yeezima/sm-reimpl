namespace SmContent.Extras.Musou;

/// <summary>
/// The Weirdboy's model for Musou Mode (added content): Exterminatus's own, in the co-op map <c>cp_arena_habs_b</c>'s archive (*supported by evidence*: <c>smtool list</c> shows the body
/// <c>elite_boy_weirdboy</c>, the staff <c>weirdboy_staff</c> and the rig <c>havok_elite_boy__hkxx</c> there; <c>ork_coop_weirdboy</c> names <c>elite_boy_weirdboy</c> and <c>havok_elite_boy</c>, its
/// staff's wargear the <c>weirdboy_staff</c> visual with no handle or body node). Multiplayer content: no singleplayer dependency. Read from the player's install at run time only.
/// </summary>
public static class WeirdboyRig
{
    public const string Archive = "data/chunks/cp_maps__cp_arena_habs_b.oppc", Entry = "havok_elite_boy__hkxx", BodyVisual = "elite_boy_weirdboy", Staff = "weirdboy_staff";

    /// <summary>
    /// The clips by the crowd's five jobs (candidates in order, then a part of a name to fall back on). The rig's clips (<c>smtool graph-dump --animations --archive
    /// data/chunks/cp_maps__cp_arena_habs_b.oppc --entry havok_elite_boy__hkxx</c>): runs and walks in four directions, <c>WIB_1HM_ACT_idle_01</c>, the teleport in and out, the summons'
    /// start, loop and release, <c>WIB_WIB_MEL_Cast_Magic_01</c>, flinches, the stun's enter, loop and exit, and <c>EB_WIB_Death_Teleport</c>. The picks are **ours**: the cast for every attack
    /// (the staff's swing, the bolt and the summons: the crowd has one attack clip), the stun's loop for a stagger and for the stun.
    /// </summary>
    public static readonly (string[] Names, string Contains) Run = (["EB_WIB_NAV_Run_F_01"], "Run_F"),
        Idle = (["WIB_1HM_ACT_idle_01"], "idle"),
        Attack = (["WIB_WIB_MEL_Cast_Magic_01", "WIB_1HM_ACT_Summon_orks_loop_01"], "Cast"),
        Stagger = (["EB_WIB_REC_Stun_Loop", "EB_WIB_REC_Stun_Enter"], "_REC_"),
        Death = (["EB_WIB_Death_Teleport"], "Death");

    /// <summary>The rig as a profile: only the data sits here; no marine or Ork graph map fits it, so nothing may read those through this profile.</summary>
    public static readonly RigProfile Profile = new("weirdboy", Archive, Entry, BodyVisual);
}
