namespace SmClient;

/// <summary>
/// M40-T3: the blend times (seconds) with which a clip takes over from whatever was shown (<see cref="CharacterViewer.Pose"/> /
/// <see cref="CharacterViewer.Play"/> cross-fade from a snapshot of the shown pose). The values are the blending transition
/// effects of the original behaviour graphs (read with <c>smtool graph-dump &lt;graph&gt; --no-tree</c>, "transitions:"); which
/// transition belongs to which client clip entry is the client's mapping (interpretation). The cross-fade never runs shorter than
/// <see cref="CharacterViewer.MinEntryBlendSeconds"/> (0.125 s, ours: it keeps a linear fade under 25 degrees per frame at 60 fps), which
/// lengthens the graphs' 0.1 s ("Default 3 Frame Transition") and 0.033 s ("Blend Slo-mo", "Blend One Frame").
/// Client presentation only: the simulation does not see these.
/// </summary>
public static class ClipBlends
{
    /// <summary>"Default 3 Frame Transition" (344 of the base graph's transitions, <c>SM_Base_MP</c>; <c>MP_SM_Reactions</c>' ranged / grenade / extended entries; the jump graph's "Default Jump to Action"): 0.1 s. Also the default for an entry whose graph value is not read.</summary>
    public const float Default = 0.1f;
    /// <summary>"Fast Blend" / "Entry to Melee" (<c>MP_SM_MEL_*</c> entry from navigation, <c>MP_SM_NAV_JP_Sim</c> ground pound impact and get-up): 0.167 s (5 frames).</summary>
    public const float Fast = 5f / 30f;

    /// <summary>A reaction (interrupt, stun, stagger, blast): "Default 3 Frame Transition" into the ranged / grenade / extended reaction states of <c>MP_SM_Reactions</c>.</summary>
    public const float Reaction = Default;
    /// <summary>The charge and its collision stop: "Default 3 Frame Transition" (the base graph's bull-rush states; the entry from sprint has no distinct effect).</summary>
    public const float Rush = Default;
    /// <summary>
    /// Animation pass 3: the charge's entry by its clip. The rush itself (<c>SM_BUL_Hit_M_*</c>) is entered from the sprint loop, by the sprint graphs' "Sprint Loop" →
    /// "Sprint Attack" on <c>BTN_Melee_Stun</c>: '8 Frame Blend' 0.267 s (<c>MP_SM_Bolter_NuSprint</c>, the heavy and cannon sprint graphs alike); the collision stop
    /// (<c>SM_NAV_Bullrush2Idle_F_Collision_01</c>, "Knocked Back") from the rush by 'Default 3 Frame Blend' 0.1 s. <c>--no-p3</c>: <see cref="Rush"/> for both.
    /// </summary>
    public static float RushFor(string clip) =>
        !CharacterViewer.NoP3 && clip.StartsWith("SM_BUL_", System.StringComparison.OrdinalIgnoreCase) ? SprintToCharge : Rush;
    /// <summary>'8 Frame Blend' (the sprint graphs' Sprint Loop → Sprint Attack, Sprint Loop → Sprint to Idle): 8 frames at 30 fps.</summary>
    public const float SprintToCharge = 8f / 30f;
    /// <summary>An evade roll or hop: "Nav to Evade" 0.1 s (<c>SM_Base_MP</c>).</summary>
    public const float Evade = Default;
    /// <summary>The grenade toss: "Default Jump to Action" / "Default 3 Frame Transition" into the toss state.</summary>
    public const float Throw = Default;
    /// <summary>The first strike of a melee string: "Entry to Melee" 0.167 s (<c>MP_SM_MEL_CombatKnife</c>, the other melee graphs alike).</summary>
    public const float MeleeEntry = Fast;
    /// <summary>A later step of the string: the chain transitions that carry a blend use "CustomTransitionEffect0x" 0.1 s; the rest have none (clips authored to run into each other), so 0.1 s is ours.</summary>
    public const float MeleeChain = Default;

    /// <summary>Jump-pack clips: the ground pound's dive, impact and get-up (<c>SM_JMP_GP_*</c>) use "Fast Blend" 0.167 s; take-off, flight and landings "Blend Launch to Jump" / "Back To Jump" 0.1 s (<c>MP_SM_NAV_JP_Sim</c>).</summary>
    public static float Jump(string clip) => clip.Contains("_GP_", System.StringComparison.OrdinalIgnoreCase) || clip.Contains("Landing_MedRun", System.StringComparison.OrdinalIgnoreCase) ? Fast : Default;

    /// <summary>The melee strike's blend: the string's first step enters from navigation, the others chain.</summary>
    public static float Melee(int step) => step <= 1 ? MeleeEntry : MeleeChain;
}
