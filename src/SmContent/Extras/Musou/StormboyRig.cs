namespace SmContent.Extras.Musou;

/// <summary>
/// The Stormboy's model for Musou Mode (added content, expansion S3): the campaign's own (*supported by evidence*: <c>smtool list</c> shows the body <c>boy_stormboy</c> (with its <c>.O3d</c> tree), its
/// textures, the jump pack's <c>stormboy_rocket</c> textures and the rig <c>havok_stormboy__hkxx</c> in <c>data/chunks/ebp_sets__s002-001.oppc</c>). A **singleplayer dependency** of Musou Mode (CLAUDE.md
/// Scope "Out"; the campaign's first Stormboy set). Read from the player's install at run time only; nothing extracted enters the repository.
/// </summary>
public static class StormboyRig
{
    public const string Archive = "data/chunks/ebp_sets__s002-001.oppc", Entry = "havok_stormboy__hkxx", BodyVisual = "boy_stormboy";

    /// <summary>
    /// The clips by the crowd's five jobs (candidates in order, then a part of a name to fall back on). The rig's 47 clips (<c>smtool graph-dump --animations --archive data/chunks/ebp_sets__s002-001.oppc
    /// --entry havok_stormboy__hkxx</c>) are all the campaign's flight and set pieces: <c>BOY_STRM_flyUp_part2_hover</c>, <c>BOY_STRM_FF_Fire_01</c>, <c>BOY_STRM_FF_Fly_Death_01</c>,
    /// <c>BOY_STRM_FreeFall_*</c>, <c>BOY_STRM_Latch_*</c>, <c>BOY_STRM_Path_*</c>; no run, idle or melee on the ground. The picks are **ours**: the hover for running, standing and the hop, the
    /// flying fire for every attack, the flying death for the stagger and the death.
    /// </summary>
    public static readonly (string[] Names, string Contains) Run = (["BOY_STRM_flyUp_part2_hover"], "hover"),
        Idle = (["BOY_STRM_flyUp_part2_hover"], "hover"),
        Attack = (["BOY_STRM_FF_Fire_01"], "Fire"),
        Stagger = (["BOY_STRM_FF_Fly_Death_01"], "Death"),
        Death = (["BOY_STRM_FF_Fly_Death_01", "BOY_STRM_flyUp_Death_03"], "Death");

    /// <summary>The rig as a profile: only the data sits here; no marine or Ork graph map fits it, so nothing may read those through this profile.</summary>
    public static readonly RigProfile Profile = new("stormboy", Archive, Entry, BodyVisual);
}
