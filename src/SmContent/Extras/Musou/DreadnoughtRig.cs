namespace SmContent.Extras.Musou;

/// <summary>
/// The Musou Dreadnought's rig, model and clip names (D2; docs/musou-dreadnought-design.md section 6). The rig, model and 155 clips are the install's (<c>ebp_sets__mp_versus_set</c>, original assets loaded at run time only,
/// never in the repo); which clip plays for which state is **ours** (picked by name from the rig's list, <c>smtool graph-dump --animations ...</c>; the behaviour graph is not evaluated: provisional approximation).
/// </summary>
public static class DreadnoughtRig
{
    public const string Archive = "data/chunks/ebp_sets__mp_versus_set.oppc", Entry = "havok_sm_dreadnought_basic__hkxx", BodyVisual = "sm_dreadnought_basic";

    /// <summary>The rig profile (the Warboss's pattern: the model and rig readers only).</summary>
    public static readonly RigProfile Profile = new("dreadnought", Archive, Entry, BodyVisual);

    public const string Entrance = "DN_NAV_Entrance", Death = "DN_REC_Death_01", IdleAim = "DN_NAV_Idle_S_AimF_01", IdleMelta = "DN_NAV_Idle_S_LAimF_01",
        Sprint = "DN_NAV_Bullrush_F", SprintLeft = "DN_NAV_Bullrush_L", SprintRight = "DN_NAV_Bullrush_R", Collision = "DN_NAV_Bullrush_F_Collision",
        Reload = "DN_RNG_Reload", FireAdditive = "DN_RNG_FireIdle_S_01", FireMeltaAdditive = "DN_RNG_LFireIdle_S_01";

    /// <summary>The clip durations the pose code needs without reading the rig (seconds; from the rig's list, 2026-11-15).</summary>
    public const float EntranceSeconds = 4.833f, DeathSeconds = 3.433f;

    /// <summary>Ground speed (m/s) the walk and run cycles are taken to cover at rate 1 (**estimate**: the data's walk 3 and run 6.5 speeds; the clips' own strides were not measured). Above <see cref="RunFrom"/> the run cycle shows.</summary>
    public const float WalkCycleSpeed = 3f, RunCycleSpeed = 6.5f, RunFrom = 4.5f;

    static readonly string[] Dirs = ["F", "FR", "R", "BR", "B", "BL", "L", "FL"];

    /// <summary>The direction's letters (F, FR, R, BR, B, BL, L, FL) of a velocity at <paramref name="degrees"/> off the facing (0 forward, 90 right; eight 45-degree sectors).</summary>
    public static string Direction(float degrees)
    {
        float d = degrees % 360f; if (d < 0) d += 360f;
        return Dirs[(int)MathF.Floor((d + 22.5f) / 45f) % 8];
    }

    /// <summary>
    /// A locomotion clip: <paramref name="run"/> (else walk), <paramref name="direction"/> (<see cref="Direction"/>), <paramref name="melta"/> (the left arm raised: the <c>LAim</c> set), <paramref name="aim"/> -1 down, 0 level, +1 up.
    /// </summary>
    public static string Move(bool run, string direction, bool melta, int aim) =>
        $"DN_NAV_{(run ? "Run" : "Walk")}_{direction}_{(melta ? "L" : "")}Aim{(aim < 0 ? "Down" : aim > 0 ? "Up" : "F_01")}";

    /// <summary>The standing clip at an aim: idle level, or the aim-up and aim-down clips (middle set).</summary>
    public static string Stand(bool melta, int aim) => aim == 0 ? (melta ? IdleMelta : IdleAim) : $"DN_NAV_{(melta ? "L" : "")}Aim{(aim < 0 ? "Down" : "Up")}_Mid";
}
